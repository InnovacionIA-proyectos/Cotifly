# Cotifly — backend .NET 10

Backend de Cotifly migrado de Python a C# / .NET 10 (milestone HU-003). Este README es el esqueleto creado en HU-003.2 (#8); las secciones marcadas se completan en HU-003.8 (#14).

## 1. Descripción

Cotifly es el bot cotizador de Botero Soto: un comercial conversa con un agente de IA (consola web de ClientePro o Google Chat), el agente reúne los datos, resuelve margen y precio, genera el PDF de la cotización y la envía por correo. Este proyecto reemplaza por completo a los servicios Python `orquestador` y `mcp_server` (corte big-bang, ADR-001).

Estado actual: solución, capas, convenciones, patrón de configuración validada al arrancar, `GET /health` y `GET /health/ready`, modelo de dominio y persistencia EF Core Database First sobre SQL Server 2019 (#9). La lógica de negocio de los módulos llega en #10 a #16.

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
- Infraestructura: `Repositories/`, `Services/`; `Persistence/` para el `DbContext` (el de `dbo_cotifly` en `Comun/Persistence/`, el de solo lectura de `dbsolucionesti` en `Identidad/Persistence/`).
- Api: `Controllers/`, `DTOs/`.

**Modelo de dominio y persistencia (#9).** Una entidad por tabla de `dbo_cotifly`, sin dependencias técnicas; setters privados, fábricas `Crear`/`Registrar`/`Emitir` que devuelven `clsResultado<T>` con las invariantes baratas (folio obligatorio ≤ 50, versión ≥ 1, TRM 0 en COP, márgenes ≥ 0 y < 1 solo con el método `margen` (`markup` admite ≥ 1, `clsReglaMargen`), correo con al menos un `para`, destinatario `para|cc`, hash SHA-256 de 64 hex...). Los enums se guardan con su código snake_case (`clsCodigoEnum`: `sin_enviar`, `politica_linea`, `gerente_comercial`), que es lo que exigen los CHECK del esquema.

| Tabla (`dbo_cotifly`) | Entidad (Dominio) | Puerto (Aplicacion) |
|---|---|---|
| `Cotizaciones` | `Cotizaciones/clsCotizacion` (+ `EstadoEnvio`, `clsDatosCotizacion`) | `IClsCotizacionRepository` |
| `MargenesPolitica` | `Margen/clsMargenPolitica` | `IClsMargenPoliticaRepository` |
| `SapMapeoTrqType` | `Margen/clsSapMapeoTrqType` | `IClsSapMapeoRepository` |
| `MargenAjustes` | `Margen/clsMargenAjuste` (+ `OrigenMargen`) | `IClsMargenAjusteRepository` |
| `Correos`, `CorreoDestinatarios` | `Correo/clsCorreo`, `clsCorreoDestinatario` (+ `EstadoCorreo`, `TipoDestinatario`) | `IClsCorreoRepository` |
| `Sesiones`, `EventosConversacion` | `Sesiones/clsSesion`, `clsEventoConversacion` (+ `EstadoSesion`) | `IClsSesionRepository` (en `Agente`) |
| `EventosHerramienta` | `Auditoria/clsEventoHerramienta` | `IClsAuditoriaRepository` |
| `UsuariosWeb` | `Identidad/clsUsuarioWeb` | `IClsUsuarioWebRepository` |
| `RolesMapeo`, `TokensRefresco`, `TokensRevocados` (script 0002) | `Identidad/clsRolMapeo`, `clsTokenRefresco`, `clsTokenRevocado` (+ `RolInterno`) | sin puerto aún (#12) |
| `Sesiones.estadoAgente` (JSON, script 0002) | `Agente/clsEstadoConversacion` (+ `clsCandidatoCliente`) | vía `clsSesion` |
| `dbsolucionesti.tblSAPUsuariosApp` (solo lectura) | `Identidad/clsUsuarioSapApp` (sin `CLAVE`) | sin puerto aún (#12) |

Los repositorios solo agregan o modifican en memoria; `IClsUnitOfWork.GuardarCambiosAsync` confirma todo en una transacción y traduce las violaciones de unicidad a `clsViolacionUnicidadException` con código de negocio (`uq_cotizaciones_folio` → `FOLIO_DUPLICADO`). `OrigenCliente` vive en `Clientes`.

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
- Despliegue: Docker Swarm + Dokploy (HU-006).

**Base de datos (ADR-008, #9).** SQL Server 2019 corporativo (`tbsdatacenter`), esquema `dbo_cotifly` dentro de `dbTarifario_Nexo_Pruebas`. EF Core **Database First**: `clsCotiflyDbContext` mapea a mano (Fluent API, `Comun/Persistence/Configurations/`) las tablas que crean los scripts; `HasDefaultSchema("dbo_cotifly")`, tablas PascalCase y columnas camelCase explícitas con su tipo SQL, `UseCompatibilityLevel(150)`. **No hay migraciones EF**: el esquema cambia solo con scripts `../migrations_mssql/000N_*.sql` idempotentes:

| Script | Contenido | Estado |
|---|---|---|
| `0001_script_bd_cotifly_sqlserver.sql` | 10 tablas, vistas y seed (HU-002.4) | aplicado en `tbsdatacenter` |
| `0002_identidad_estado_agente.sql` | `Sesiones.estadoAgente` (JSON) y `Sesiones.usuarioId`; `RolesMapeo`, `TokensRefresco`, `TokensRevocados`; índices de consulta | **extensión aditiva pendiente de aprobación** (Sara + scalderon-bs). Paso de un solo sentido: después del 0002 **no** se re-ejecuta el 0001 (ver encabezado del 0002 y ADR-008) |

- `updatedAt` (y `createdAt` si la entidad no lo trae) los fija `clsMarcasTiempoInterceptor` con `IClsReloj`: el esquema no tiene triggers.
- `Sesiones.estadoAgente` guarda `clsEstadoConversacion` como JSON camelCase (`clsConvertidorEstadoConversacion`).
- `clsIdentidadDbContext` (solo lectura, `NoTracking`, `SaveChanges` lanza) lee `tblSAPUsuariosApp` de `dbsolucionesti`. El esquema y la tabla son configurables (`Identidad:Esquema`, por defecto `dbo`; `Identidad:Tabla`, por defecto `tblSAPUsuariosApp`) mientras el DBA confirma la ubicación real.
- `DWCubosEBSSAP` (`ConnectionStrings:Regional`, regional del comercial) llega con #12.

**Cadenas de conexión** (`clsOpcionesConexiones`, variables en `.env.example`):

| Clave | Uso | Variable |
|---|---|---|
| `ConnectionStrings:Cotifly` | `dbo_cotifly`, lectura y escritura | `ConnectionStrings__Cotifly` |
| `ConnectionStrings:Identidad` | `dbsolucionesti`, solo lectura | `ConnectionStrings__Identidad` |

Siempre SQL login (`cotifly_app`, pendiente con el DBA): el contenedor Linux no admite autenticación integrada. En `Production` la API no arranca si una cadena falta, es un placeholder, usa `Trusted_Connection`/`Integrated Security` o su `Password` es de ejemplo. Fuera de `Production` pueden quedar vacías: la API arranca y `GET /health/ready` responde 503.

Se completa en HU-003.8 (#14).

## 4. Flujo del sistema

Se completa en HU-003.8 (#14).

## 5. Stack

- .NET 10 / ASP.NET Core 10, C# `latest`, `Nullable` y `TreatWarningsAsErrors` activos, `AnalysisLevel` fijo en `10.0` (`Directory.Build.props`).
- Configuración: `Microsoft.Extensions.Options` con DataAnnotations y `ValidateOnStart`.
- Central Package Management con versiones fijas (`Directory.Packages.props`).
- Persistencia: EF Core 10 (`Microsoft.EntityFrameworkCore.SqlServer`), health checks (`Microsoft.Extensions.Diagnostics.HealthChecks`).
- Pruebas: xUnit v3 sobre Microsoft.Testing.Platform, AwesomeAssertions (Apache-2.0), ArchUnitNET, `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.OpenApi` + `Microsoft.OpenApi.YamlReader`, Testcontainers.MsSql (SQL Server 2019), Reqnroll (`Reqnroll.xunit.v3`).
- Planeado (se agrega con cada HU): Microsoft.Extensions.AI + Semantic Kernel, PDFsharp-MigraDoc, MailKit, NSubstitute.

## 6. Ejecución local

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

- Se monta el repo completo (no solo `backend-dotnet/`) porque las pruebas copian `../migrations_mssql/*.sql` para aplicarlas en Testcontainers.
- El socket de Docker y `TESTCONTAINERS_HOST_OVERRIDE` permiten que las pruebas de integración levanten SQL Server 2019 como contenedor hermano (detalle en §8).
- El volumen `cotifly-nuget` conserva la caché de NuGet entre ejecuciones. Las carpetas `bin/` y `obj/` que crea el contenedor están en `.gitignore`.

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

**Configuración validada al arrancar** (`src/Cotifly.Infraestructura/Configuracion/`): cada sección se enlaza a una clase `clsOpcionesXxx` con DataAnnotations y se registra con `AddOpcionesValidadas<T>()`, que agrega `ValidateOnStart` y el validador de secretos. En `Production`, una propiedad marcada con `[clsSecreto]` vacía, con placeholder (`<secreto>`, `cambiar…`, `changeme`, `dev`) o más corta que su mínimo impide arrancar; el mensaje nombra la clave, nunca el valor. Opciones actuales: `Zona:ZonaHoraria` (por defecto `America/Bogota`, debe existir en el sistema), `ConnectionStrings:Cotifly` / `ConnectionStrings:Identidad` (`[clsSecreto]` + `clsValidadorOpcionesConexiones`, ver §3) e `Identidad:Esquema` / `Identidad:Tabla`. #12 agrega las suyas siguiendo los pasos documentados en `clsRegistroOpciones`.

Levantar la API contra un SQL Server local de pruebas: pasar `-e ConnectionStrings__Cotifly="Server=host.docker.internal,1433;Database=dbTarifario_Nexo_Pruebas;User Id=...;Password=...;TrustServerCertificate=True"` al `docker run` anterior y consultar `curl http://localhost:8080/health/ready`.

**Abrir la solución en un IDE:**
- Requiere el SDK .NET 10 en el host. Se puede instalar por usuario, sin permisos de administrador, con el script oficial `dotnet-install` (`dotnet-install.ps1 -Channel 10.0 -InstallDir $env:LOCALAPPDATA\dotnet`).
- IDE: Visual Studio 2022 17.14 o posterior, Visual Studio 2026 o una versión reciente de Rider.
- Si alternas entre compilar en el host y en el contenedor, borra antes las carpetas `bin/` y `obj/`: las rutas del restore (`project.assets.json`) no son compatibles entre ambos.

## 7. API

El contrato congelado es `specs/openapi.yaml` (#17), fuente de verdad (SDD, ADR-010). Hoy está implementado:

| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/health` | `200 {"status":"ok","servicio":"cotifly-api","entorno":"<ASPNETCORE_ENVIRONMENT>"}` |
| GET | `/health/ready` | `200 {"status":"ready"}` si `dbo_cotifly` acepta conexiones y tiene el 0002 aplicado; `503 {"status":"unavailable","detalle":"..."}` si no (sin cadena configurada, base caída o `esquema incompleto: falta 0002`) |

`/health/ready` ejecuta los health checks con etiqueta `ready` (hoy `clsBaseDatosHealthCheck`: `CanConnectAsync` sobre `clsCotiflyDbContext` más verificación de `Sesiones.estadoAgente` y `TokensRefresco`, con un límite de 5 s para toda la sonda). `detalle` nunca incluye la cadena ni el servidor; la excepción queda en el log. Ninguno de los dos exige autenticación.

El resto de endpoints: se completa en HU-003.8 (#14).

## 8. Testing

| Proyecto | Qué cubre hoy | Unitarias | Integración |
|---|---|---|---|
| `Cotifly.Dominio.Tests` | `clsResultado<T>`, `clsErrorDominio`, `clsCodigoEnum` (códigos de los enums) e invariantes de las entidades (cotización, correo, margen, sesión, identidad, tokens), regla de margen por método (`margen` < 1, `markup` ≥ 1) | 117 | — |
| `Cotifly.Aplicacion.Tests` | Registro de dependencias de Aplicación | 2 | — |
| `Cotifly.Infraestructura.Tests` | `clsRelojSistema`, registro de dependencias (puertos, contextos), validador de secretos, opciones `Zona` y `ConnectionStrings`, convertidores, traducción de violaciones de unicidad. **Integración:** scripts 0001+0002 aplicados (y 0002 idempotente), deriva modelo EF ↔ catálogo en ambos sentidos (tablas, columnas, tipos, nulabilidad, collation, nombres y columnas de PK/FK, índices únicos/filtrados/`ix_` con orden e `INCLUDE`, CHECK), idempotencia del 0002 con foto completa del esquema, cada método de cada repositorio, aislamiento por dueño, folio duplicado → `FOLIO_DUPLICADO`, dedupe de correo con reloj falso, interceptor de `updatedAt`, PK/FK compuestas, CHECK JSON, contexto de identidad de solo lectura, health check (incluye base con solo el 0001 → esquema incompleto, y límite de tiempo de la sonda) | 65 | 63 |
| `Cotifly.Api.Tests` | `GET /health`, `GET /health/ready` (503 sin cadena y con base inalcanzable; con SQL Server real: **200**, y 503 `esquema incompleto: falta 0002` en una base con solo el 0001), composition root, arranque con configuración inválida, contrato `specs/openapi.yaml` (`/health` y `/health/ready` declarados, respuestas reales cumplen el esquema) | 14 | 2 |
| `Cotifly.Arquitectura.Tests` | Dependencias entre capas (ArchUnitNET + referencias de ensamblado), Dominio sin paquetes externos y prefijos `cls`/`ICls` | 14 | — |
| `Cotifly.Aceptacion.Tests` | `specs/features/persistencia.feature` con Reqnroll: guardar/recuperar, folio único, aislamiento por comercial, `updatedAt` | — | 4 |

- TDD: test en rojo, implementación, refactor (ADR-010).
- **BDD (Reqnroll):** los `.feature` viven en `specs/features/` (fuente de verdad SDD) y `Cotifly.Aceptacion.Tests` los enlaza; el code-behind se genera en `obj/`. Se usa `Reqnroll.xunit.v3` (Reqnroll 3.x soporta xUnit v3 y corre sobre MTP como el resto), así que no hace falta MSTest. Los pasos usan el `AddInfraestructura` real contra SQL Server en Testcontainers.
- **Integración (Testcontainers.MsSql):** imagen `mcr.microsoft.com/mssql/server:2019-latest`. `tests/Comun/clsBaseDatosPruebas` crea `dbTarifario_Nexo_Pruebas`, aplica `0001` y `0002` partidos por líneas `GO` (como sqlcmd) y crea una `dbsolucionesti` mínima con `dbo.tblSAPUsuariosApp`. Un contenedor por proyecto de pruebas (colección xUnit `SqlServer`), que solo se crea si corre alguna prueba de integración. Cada prueba usa datos propios (folios y usuarios únicos).
- Los proyectos con integración declaran `<UsaSqlServerPruebas>true</UsaSqlServerPruebas>`: `tests/Directory.Build.props` agrega Testcontainers, el soporte compartido de `tests/Comun/` (contenedor, reloj falso, DI de pruebas) y copia `../migrations_mssql/*.sql` a la salida.
- Las pruebas de integración y de aceptación llevan `[Trait("Categoria","Integracion")]` (en aceptación, a nivel de ensamblado).
- `global.json` activa Microsoft.Testing.Platform (MTP) para `dotnet test` (obligatorio con xUnit v3 en el SDK 10).
- Filtros con opciones de MTP: `dotnet test --filter-not-trait "Categoria=Integracion" --ignore-exit-code 8` (sin integración: no necesita Docker) o `--filter-trait "Categoria=Integracion" --ignore-exit-code 8` (solo integración). `--ignore-exit-code 8` evita que falle la corrida por los proyectos que quedan con cero pruebas tras el filtro. Por clase o método: `--filter-class` / `--filter-method`.
- Auditoría NuGet: las advertencias de vulnerabilidades (NU1900–NU1904) siguen visibles pero no rompen el build local ni el de Docker (`WarningsNotAsErrors` en `Directory.Build.props`). CI tendrá un job de auditoría estricta (#14), junto con los reportes TRX y de cobertura. Con xUnit v3 sobre MTP el reporte TRX es nativo: `dotnet test -- --report-xunit-trx` (verificado; `--report-trx` no existe sin la extensión `Microsoft.Testing.Extensions.TrxReport` y termina con código 5).

**Ejecutar Testcontainers desde el contenedor del SDK** (Docker Desktop en Windows, PowerShell). Comando verificado:

```powershell
docker pull mcr.microsoft.com/mssql/server:2019-latest   # una vez
docker run --rm `
  -v //var/run/docker.sock:/var/run/docker.sock `
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
  -e REQNROLL_TELEMETRY_ENABLED=0 `
  -v "C:\...\Cotifly\Cotifly:/repo" `
  -v cotifly-nuget:/root/.nuget/packages `
  -w /repo/backend-dotnet mcr.microsoft.com/dotnet/sdk:10.0 `
  sh -c "dotnet build -c Release && dotnet test -c Release --no-build"
```

- `//var/run/docker.sock` (doble barra en PowerShell) da acceso al Docker del host: SQL Server corre como contenedor hermano.
- `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal` hace que las pruebas se conecten al puerto publicado en el host y no a `localhost` del contenedor del SDK.
- Si `host.docker.internal` no resuelve (Docker en Linux sin Desktop), agregar `--add-host=host.docker.internal:host-gateway`, o usar `--network host` sin la variable. Si el contenedor Ryuk (limpieza de Testcontainers) no logra conectarse, `-e TESTCONTAINERS_RYUK_DISABLED=true` (los contenedores de prueba se borran igual al terminar, salvo que la corrida se aborte).
- `REQNROLL_TELEMETRY_ENABLED=0` desactiva la telemetría anónima de Reqnroll. Sin `APPDATA` (contenedor del SDK) Reqnroll puede dejar `tests/Cotifly.Aceptacion.Tests/Reqnroll/userid`; está en `.gitignore`.

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
│   ├── features/                # escenarios Gherkin (Reqnroll): persistencia.feature
│   └── decisiones/              # ADR-001 … ADR-011
├── src/
│   ├── Cotifly.Dominio/         # Comun/ + entidades y enums por módulo
│   ├── Cotifly.Aplicacion/      # Comun/{Interfaces,Exceptions}/ + <Módulo>/Repositories/ (puertos) + clsDependenciasAplicacion
│   ├── Cotifly.Infraestructura/ # Comun/Persistence/ (DbContext, configuraciones EF, UoW, interceptor, health check),
│   │                            # Configuracion/, <Módulo>/Repositories/, Identidad/Persistence/ + clsDependenciasInfraestructura
│   └── Cotifly.Api/             # Program.cs, Sistema/ (health) + carpetas por módulo
└── tests/
    ├── Comun/                   # soporte compartido de integración (enlazado, no es proyecto)
    ├── Cotifly.Dominio.Tests/
    ├── Cotifly.Aplicacion.Tests/
    ├── Cotifly.Infraestructura.Tests/
    ├── Cotifly.Api.Tests/
    ├── Cotifly.Arquitectura.Tests/
    └── Cotifly.Aceptacion.Tests/

../migrations_mssql/             # scripts de esquema SQL Server (0001, 0002): única vía de cambio de esquema
```
