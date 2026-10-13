# Cotifly — backend .NET 10

Backend de Cotifly, el bot cotizador de Botero Soto, migrado de Python a C# / .NET 10 (milestone HU-003). Un comercial captura la solicitud en la consola web de ClientePro; Nexo costea, el backend resuelve margen y precio, genera el PDF y lo envía por correo, y la IA solo redacta textos y asiste sobre una cotización ya hecha ([ADR-018](specs/decisiones/ADR-018-pipeline-determinista-ia-etapa-final.md)). Google Chat y el agente cotizador conversacional se retiraron en HU-013.9 (#105). Reemplaza a los servicios Python `orquestador` y `mcp_server` (corte big-bang, [ADR-001](specs/decisiones/ADR-001-estrategia-de-corte.md)).

- Clean Architecture de 4 capas en un monolito modular: `Cotifly.Dominio`, `Cotifly.Aplicacion`, `Cotifly.Infraestructura`, `Cotifly.Api` ([ADR-002](specs/decisiones/ADR-002-arquitectura-clean-monolito-modular.md)).
- ASP.NET Core 10, EF Core 10 Database First sobre SQL Server 2019, Semantic Kernel + Microsoft.Extensions.AI, PDFsharp-MigraDoc, MailKit.
- Un solo contenedor (`Dockerfile` multi-stage, runtime chiseled, sin shell, usuario no root); sin `docker-compose` ([ADR-007](specs/decisiones/ADR-007-contenedores-sin-docker-compose.md)).
- Contrato HTTP congelado en [`specs/openapi.yaml`](specs/openapi.yaml); escenarios BDD en [`specs/features/`](specs/features/).

## Inicio rápido

La máquina de desarrollo no necesita el SDK .NET 10: todo corre en contenedores. Desde la raíz del repo, en PowerShell:

```powershell
# Entorno local completo: SQL Server 2019 + esquema + dbsolucionesti de prueba + API en http://localhost:8080
.\backend-dotnet\scripts\levantar-local.ps1
Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/auth/login-clientepro -ContentType 'application/json' -Body '{"idUsuario":"APEREZ"}'
.\backend-dotnet\scripts\levantar-local.ps1 -Bajar

# Build y todas las pruebas (unitarias + integración con Testcontainers)
docker run --rm -v //var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
  -e REQNROLL_TELEMETRY_ENABLED=0 -v "${PWD}:/repo" -v cotifly-nuget:/root/.nuget/packages `
  -w /repo/backend-dotnet mcr.microsoft.com/dotnet/sdk:10.0 sh -c "dotnet build -c Release && dotnet test -c Release --no-build"

# Solo la imagen
docker build -t cotifly-api:dev -f backend-dotnet/Dockerfile backend-dotnet
```

En Linux, macOS o Git Bash: `sh backend-dotnet/scripts/levantar-local.sh` (y `--bajar`). Detalle en [docs/06-ejecucion-local.md](docs/06-ejecucion-local.md).

## Documentación

| Sección | Contenido |
|---|---|
| [1. Descripción](docs/01-descripcion.md) | Objetivo, roles, capacidades, estado por issue, alcance y glosario |
| [2. Arquitectura](docs/02-arquitectura.md) | Capas, módulos, convenciones, modelo de dominio, cada módulo de negocio, turno del asistente por folio (`/web/chat`), login SSO, validador del agente e integración con Nexo (diagramas Mermaid) |
| [3. Infraestructura](docs/03-infraestructura.md) | Imagen Docker, variables de entorno, SQL Server (`dbo_cotifly`, `dbsolucionesti`, `DWCubosEBSSAP`), configuración por área y servicios externos |
| [4. Flujo del sistema](docs/04-flujo-del-sistema.md) | Login, nueva cotización, mis cotizaciones, margen, correo, estados y procesos en segundo plano |
| [5. Stack](docs/05-stack.md) | Tecnologías, paquetes NuGet con versión y licencia, imágenes |
| [6. Ejecución local](docs/06-ejecucion-local.md) | Scripts de entorno local, build y pruebas en el SDK, imagen, IDE |
| [7. API](docs/07-api.md) | Endpoints, autenticación, errores, ejemplos y límites (el detalle está en `specs/openapi.yaml` y `docs/docs_front/CONTRATO_API_WEB.md`) |
| [8. Testing](docs/08-testing.md) | Pirámide, proyectos, traits, comandos, Testcontainers, Reqnroll, cobertura, CI y paridad con el Python (histórica) |
| [9. Estructura del proyecto](docs/09-estructura-del-proyecto.md) | Árbol de carpetas y dónde va cada cosa |
| [10. Operación](docs/10-operacion.md) | Salud, logs, métricas, depuración sin shell, riesgos, decisiones pendientes, activación y problemas frecuentes |
| [QA: regresión manual](docs/qa/regresion-manual.md) | Casos de los 3 flujos principales para la ronda de Kevin (HU-003.8) |
| [QA: paridad Python vs .NET](docs/qa/paridad-python-dotnet.md) | HU-003.9 (#15): cerrada por decisión del usuario (2026-10-13, HU-022.1): el Python se retiró del repositorio (tag `python-final`), así que ya no hay contra qué comparar. Se conserva como histórico |
| [Decisiones (ADR)](specs/decisiones/README.md) | ADR-001 a ADR-012 |

## Convenciones en una línea

Clases `cls*` e interfaces `ICls*` (el build falla si no), tablas PascalCase y columnas camelCase, esquema solo por scripts `db/migrations_mssql/000N_*.sql` (nunca migraciones EF), toda versión NuGet en `Directory.Packages.props`, cero warnings, ningún secreto en `appsettings*.json`, un cambio de contrato empieza en `specs/openapi.yaml`, y cada cambio de comportamiento trae su prueba ([ADR-010](specs/decisiones/ADR-010-metodologia-sdd-tdd-bdd.md)). Detalle en [docs/02-arquitectura.md](docs/02-arquitectura.md#24-convenciones-de-nombres).

## Integración continua

[`.github/workflows/backend-dotnet.yml`](../.github/workflows/backend-dotnet.yml) en PR y push hacia `QA` y `main`: build con warnings como errores, pruebas unitarias, pruebas de integración con Testcontainers, cobertura unida con umbral de 90 % en `Cotifly.Dominio`, auditoría estricta de vulnerabilidades NuGet, `docker build` con prueba de humo y `redocly lint` del contrato. Detalle en [docs/08-testing.md](docs/08-testing.md#87-integración-continua).

## Estado de HU-003.8 (#14)

| Criterio del issue | Estado | Evidencia |
|---|---|---|
| `Dockerfile` multi-stage (build con SDK, runtime sin SDK) y `docker-compose.yml` para ejecución local | Cumplido con desviación | `Dockerfile` de #27 (build `--no-cache` verificado en #14). Sin `docker-compose` por decisión registrada en [ADR-007](specs/decisiones/ADR-007-contenedores-sin-docker-compose.md); en su lugar `scripts/levantar-local.ps1` y `.sh` levantan SQL Server 2019, el esquema y la API con `docker run` (desarrollo y QA local) |
| Pruebas unitarias de los módulos de negocio y de integración de `/web/*`, ejecutables automáticamente en CI | Cumplido | 1845 pruebas en verde el 2026-09-28 (1488 unitarias y 356 de integración; 1 omitida a propósito, `Categoria=sap`), ya con la integración de Nexo ([docs/08-testing.md](docs/08-testing.md#82-proyectos-y-alcance)); jobs `unitarias` e `integracion` del CI con reportes TRX y cobertura (97,5 % de líneas en total, 99,3 % en Dominio) |
| Documentación técnica con las 9 secciones del estándar, sin emojis | Cumplido | `docs/01` a `docs/09`, más `docs/10-operacion.md` |
| Ronda de regresión manual con Kevin (QA) de los 3 flujos principales en un entorno de pruebas | **Pendiente** | Casos listos en [docs/qa/regresion-manual.md](docs/qa/regresion-manual.md); requiere el backend desplegado en pruebas (HU-006) |
