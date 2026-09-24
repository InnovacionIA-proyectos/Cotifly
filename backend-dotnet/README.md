# Cotifly — backend .NET 10

Backend de Cotifly migrado de Python a C# / .NET 10 (milestone HU-003). Este README es el esqueleto creado en HU-003.2 (#8); las secciones marcadas se completan en HU-003.8 (#14).

## 1. Descripción

Cotifly es el bot cotizador de Botero Soto: un comercial conversa con un agente de IA (consola web de ClientePro o Google Chat), el agente reúne los datos, resuelve margen y precio, genera el PDF de la cotización y la envía por correo. Este proyecto reemplaza por completo a los servicios Python `orquestador` y `mcp_server` (corte big-bang, ADR-001).

Estado actual: solución, capas, convenciones, patrón de configuración validada al arrancar y `GET /health`. `GET /health/ready` llega en #9; los módulos de negocio, en #9 a #16.

## 2. Arquitectura

Clean Architecture de 4 capas en un monolito modular (ADR-002):

| Proyecto | Responsabilidad | Depende de |
|---|---|---|
| `Cotifly.Dominio` | Entidades, value objects, reglas puras, `clsResultado<T>`, `clsErrorDominio` | nada |
| `Cotifly.Aplicacion` | Casos de uso, puertos `ICls*`, DTOs, validadores | Dominio |
| `Cotifly.Infraestructura` | Adaptadores de los puertos (EF Core, HTTP, PDF, correo, IA) | Aplicacion, Dominio |
| `Cotifly.Api` | Controllers, middleware, composition root | Aplicacion; Infraestructura solo para `AddInfraestructura()` |

Dominio, Aplicacion e Infraestructura tienen una carpeta por módulo: `Cotizaciones`, `Tarifas`, `Margen`, `Precio`, `Pdf`, `Correo`, `Auditoria`, `Identidad`, `Agente`, `Clientes`, `Administracion`, `Metricas`. Api solo tiene carpetas de los módulos con endpoints en `specs/openapi.yaml`, más `Sistema/` (health). Además: `Comun/` para lo transversal, `Sesiones/` solo en Dominio y `Configuracion/` en Infraestructura. Mientras un módulo no tiene código, su carpeta solo contiene un `LEEME.md` con su propósito y subcarpetas (en inglés en todas las capas):

- Dominio: clases en la carpeta; si crece, `Entities/`, `ValueObjects/`, `Rules/`.
- Aplicacion: `DTOs/`, `Validators/`, `Services/`, `Repositories/` (puertos), `Interfaces/`.
- Infraestructura: `Repositories/`, `Services/`.
- Api: `Controllers/`, `DTOs/`.

**Convenciones de nombres** (ADR-002), exigidas por `.editorconfig` (IDE1006 con severidad `error`, rompe el build) y por `Cotifly.Arquitectura.Tests`:

| Símbolo | Regla | Ejemplo |
|---|---|---|
| Clases, records, clases estáticas, atributos, clases de prueba | `cls` + PascalCase | `clsCotizacionService`, `clsSecretoAttribute`, `clsResultadoTests` |
| Interfaces | `ICls` + PascalCase | `IClsCotizacionRepository` |
| Structs, enums, delegados | PascalCase | `EstadoEnvio` |
| Miembros no privados, constantes | PascalCase | `AhoraUtc`, `NombreServicio` |
| Campos privados (instancia y estáticos) | `_camelCase` | `_timeProvider` |
| Parámetros y variables locales | camelCase | `servicios` |
| Parámetros de tipo | `T` + PascalCase | `TValor` |
| Namespaces | file-scoped (IDE0161, error) | `namespace Cotifly.Dominio.Comun;` |

En los controllers la ruta siempre es explícita (`[Route("health")]`); nunca `[Route("[controller]")]`, porque el prefijo `cls` terminaría en la URL.

Única excepción: la clase `Program` que genera el compilador para las instrucciones de nivel superior (`Program.cs` suprime IDE1006 solo en su primera línea). En .NET 10 `Program` ya es pública, así que no se declara `public partial class Program`.

Diagramas C4 y detalle por módulo: se completa en HU-003.8 (#14).

## 3. Infraestructura

- Un solo `Dockerfile` multi-stage (sdk:10.0 → aspnet:10.0) y sin `docker-compose` (ADR-007). El Dockerfile llega en #27.
- Base de datos: SQL Server 2019 corporativo, esquema `dbo_cotifly`, EF Core Database First (ADR-008, #9).
- Despliegue: Docker Swarm + Dokploy (HU-006).

Se completa en HU-003.8 (#14).

## 4. Flujo del sistema

Se completa en HU-003.8 (#14).

## 5. Stack

- .NET 10 / ASP.NET Core 10, C# `latest`, `Nullable` y `TreatWarningsAsErrors` activos, `AnalysisLevel` fijo en `10.0` (`Directory.Build.props`).
- Configuración: `Microsoft.Extensions.Options` con DataAnnotations y `ValidateOnStart`.
- Central Package Management con versiones fijas (`Directory.Packages.props`).
- Pruebas: xUnit v3 sobre Microsoft.Testing.Platform, AwesomeAssertions (Apache-2.0), ArchUnitNET, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.OpenApi` + `Microsoft.OpenApi.YamlReader`.
- Planeado (se agrega con cada HU): EF Core SqlServer, Microsoft.Extensions.AI + Semantic Kernel, PDFsharp-MigraDoc, MailKit, NSubstitute, Testcontainers.MsSql, Reqnroll.

## 6. Ejecución local

La máquina de desarrollo solo tiene SDK .NET 8, así que todo se compila y prueba dentro de la imagen oficial del SDK 10 (ADR-007). En Windows, desde PowerShell (las rutas con espacios se rompen en Git Bash):

```powershell
docker run --rm `
  -v "<ruta>\Cotifly\backend-dotnet:/src" `
  -v cotifly-nuget:/root/.nuget/packages `
  -w /src mcr.microsoft.com/dotnet/sdk:10.0 `
  sh -c "dotnet restore && dotnet build -c Release --no-restore && dotnet test -c Release --no-build"
```

El volumen `cotifly-nuget` conserva la caché de NuGet entre ejecuciones. Las carpetas `bin/` y `obj/` que crea el contenedor están en `.gitignore`.

Levantar la API sin Dockerfile (mientras llega #27):

```powershell
docker run --rm -p 8080:8080 `
  -v "<ruta>\Cotifly\backend-dotnet:/src" -v cotifly-nuget:/root/.nuget/packages `
  -w /src -e ASPNETCORE_ENVIRONMENT=Development -e ASPNETCORE_HTTP_PORTS=8080 `
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet run --project src/Cotifly.Api
# en otra terminal
curl http://localhost:8080/health
```

**Variables de entorno:** se documentan en `.env.example` (copiar a `.env`, que no se versiona). Ningún secreto va en `appsettings*.json` ni en el código.

**Configuración validada al arrancar** (`src/Cotifly.Infraestructura/Configuracion/`): cada sección se enlaza a una clase `clsOpcionesXxx` con DataAnnotations y se registra con `AddOpcionesValidadas<T>()`, que agrega `ValidateOnStart` y el validador de secretos. En `Production`, una propiedad marcada con `[clsSecreto]` vacía, con placeholder (`<secreto>`, `cambiar…`, `changeme`, `dev`) o más corta que su mínimo impide arrancar; el mensaje nombra la clave, nunca el valor. Hoy existe una opción real, `Zona:ZonaHoraria` (por defecto `America/Bogota`, debe existir en el sistema). #9 y #12 agregan las suyas siguiendo los pasos documentados en `clsRegistroOpciones`.

**Abrir la solución en un IDE:**
- Requiere el SDK .NET 10 en el host. Se puede instalar por usuario, sin permisos de administrador, con el script oficial `dotnet-install` (`dotnet-install.ps1 -Channel 10.0 -InstallDir $env:LOCALAPPDATA\dotnet`).
- IDE: Visual Studio 2022 17.14 o posterior, Visual Studio 2026 o una versión reciente de Rider.
- Si alternas entre compilar en el host y en el contenedor, borra antes las carpetas `bin/` y `obj/`: las rutas del restore (`project.assets.json`) no son compatibles entre ambos.

## 7. API

El contrato congelado es `specs/openapi.yaml` (#17), fuente de verdad (SDD, ADR-010). Hoy está implementado:

| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/health` | `200 {"status":"ok","servicio":"cotifly-api","entorno":"<ASPNETCORE_ENVIRONMENT>"}` |

`GET /health/ready` (readiness con base de datos) llega en #9.

El resto de endpoints: se completa en HU-003.8 (#14).

## 8. Testing

| Proyecto | Qué cubre hoy |
|---|---|
| `Cotifly.Dominio.Tests` | `clsResultado<T>`, `clsErrorDominio` |
| `Cotifly.Aplicacion.Tests` | Registro de dependencias de Aplicación |
| `Cotifly.Infraestructura.Tests` | `clsRelojSistema`, registro de dependencias, validador de secretos, opción `Zona` |
| `Cotifly.Api.Tests` | `GET /health` con `WebApplicationFactory`, composition root, arranque con configuración inválida, contrato `specs/openapi.yaml` (parsea sin errores, declara `/health`, la respuesta real cumple el esquema) |
| `Cotifly.Arquitectura.Tests` | Dependencias entre capas (ArchUnitNET + referencias de ensamblado), Dominio sin paquetes externos y prefijos `cls`/`ICls` |

- TDD: test en rojo, implementación, refactor (ADR-010).
- `Cotifly.Aceptacion.Tests` (Reqnroll) se crea con el primer `.feature` de `specs/features/`.
- Las pruebas de integración (Testcontainers) llevarán `[Trait("Categoria","Integracion")]`.
- `global.json` activa Microsoft.Testing.Platform (MTP) para `dotnet test` (obligatorio con xUnit v3 en el SDK 10).
- Filtros con opciones de MTP: `dotnet test --filter-not-trait "Categoria=Integracion"` (sin integración) o `--filter-trait "Categoria=Integracion"` (solo integración). Por clase o método: `--filter-class` / `--filter-method`.
- Auditoría NuGet: las advertencias de vulnerabilidades (NU1900–NU1904) siguen visibles pero no rompen el build local ni el de Docker (`WarningsNotAsErrors` en `Directory.Build.props`). CI tendrá un job de auditoría estricta (#14), junto con los reportes TRX y de cobertura.

## 9. Estructura del proyecto

```
backend-dotnet/
├── Cotifly.sln
├── global.json                  # SDK 10.0.x y runner Microsoft.Testing.Platform
├── Directory.Build.props        # net10.0, Nullable, warnings como errores, analizadores
├── Directory.Packages.props     # versiones NuGet centralizadas
├── .editorconfig                # convenciones de nombres y namespaces (error de build)
├── .gitattributes               # finales de línea LF, binarios
├── .env.example                 # variables de entorno documentadas
├── specs/
│   ├── openapi.yaml             # contrato HTTP (#17)
│   └── decisiones/              # ADR-001 … ADR-011
├── src/
│   ├── Cotifly.Dominio/         # Comun/ + carpetas por módulo
│   ├── Cotifly.Aplicacion/      # Comun/Interfaces/ + carpetas por módulo + clsDependenciasAplicacion
│   ├── Cotifly.Infraestructura/ # Comun/, Configuracion/ + carpetas por módulo + clsDependenciasInfraestructura
│   └── Cotifly.Api/             # Program.cs, Sistema/ (health) + carpetas por módulo
└── tests/
    ├── Cotifly.Dominio.Tests/
    ├── Cotifly.Aplicacion.Tests/
    ├── Cotifly.Infraestructura.Tests/
    ├── Cotifly.Api.Tests/
    └── Cotifly.Arquitectura.Tests/
```
