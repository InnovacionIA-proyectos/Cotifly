# ADR-002 — Clean Architecture de 4 capas y monolito modular

- **Estado:** Aceptada
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Se implementa en #8 (HU-003.2).

## Contexto

El estándar corporativo `dotnet-enterprise-architect` exige Clean Architecture de 4 capas, un monolito modular moderno (sin microservicios), no usar CQRS salvo justificación y el prefijo `cls` en las clases. Hoy el backend Python está repartido en dos servicios (`orquestador` FastAPI y `mcp_server` FastMCP) más `shared/`. El desarrollo .NET vive en el mismo repo `Cotifly` (decisión ya marcada en #7).

## Decisión

**Ubicación:** carpeta `backend-dotnet/` del repo `Cotifly`, con su propia `Cotifly.sln`.

**Capas (proyectos) y dependencias permitidas:**

| Proyecto | Responsabilidad | Depende de |
|---|---|---|
| `Cotifly.Dominio` | Entidades, value objects (folio, estado de conversación), reglas puras (precio, reglas de margen), códigos de error | nada |
| `Cotifly.Aplicacion` | Casos de uso, puertos (`ICls*`), DTOs, validadores, herramientas del agente | Dominio |
| `Cotifly.Infraestructura` | EF Core, clientes HTTP (SAP TM, Nexo, Grovity, Google Chat), PDF, correo, proveedores de IA | Aplicacion |
| `Cotifly.Api` | Controllers, middleware, configuración, DI (composition root) | Aplicacion; Infraestructura solo para registrar DI |

- No hay lógica de negocio en `Api` y `Dominio` no accede a datos.
- Un test de arquitectura valida estas reglas en cada build.
  - Librería recomendada: **ArchUnitNET**, que se mantiene activamente.
  - Alternativa: NetArchTest.eNhancedEdition.
  - No se usa `NetArchTest.Rules` original porque no tiene mantenimiento desde 2021.

**Monolito modular:** cada área de negocio es un módulo con carpeta propia en cada capa: `Cotizaciones`, `Tarifas`, `Margen`, `Precio`, `Pdf`, `Correo`, `Auditoria`, `Identidad`, `Agente`, `Clientes`, `Administracion`, `Metricas`.

- Carpetas adicionales (decidido en #8):
  - `Comun/` en cada capa para lo transversal (`clsResultado<T>`, `clsErrorDominio`, `IClsReloj`…); `Configuracion/` en Infraestructura para las opciones validadas al arrancar.
  - `Sesiones/` solo en Dominio (entidades `Sesiones`/`EventosConversacion`); sus casos de uso viven en `Agente`.
  - `Sistema/` solo en Api (`GET /health`, y `/health/ready` en #9).
  - En Api solo existen carpetas de módulos con endpoints en `specs/openapi.yaml`; Dominio, Aplicacion e Infraestructura tienen los 12 módulos.
- Subcarpetas en inglés en todas las capas, coherentes con los sufijos de clase (`clsCotizacionService`, `IClsCotizacionRepository`):
  - Dominio: clases en la carpeta del módulo; si crece, `Entities/`, `ValueObjects/`, `Rules/`.
  - Aplicacion: `DTOs/`, `Validators/`, `Services/`, `Repositories/` (puertos), `Interfaces/` (otros puertos).
  - Infraestructura: `Repositories/`, `Services/`.
  - Api: `Controllers/`, `DTOs/` (solo si el contrato HTTP difiere del DTO de Aplicación).
- Los módulos se comunican por interfaces de Aplicación, nunca por tablas de otro módulo.
- El `mcp_server` desaparece como servicio: sus herramientas quedan como casos de uso dentro del monolito.
- No se prepara el proyecto para partirlo en microservicios.

**Sin CQRS ni MediatR.** Los casos de uso son servicios de aplicación (`clsCotizacionService`, `clsMargenResolverService`...). El dominio no justifica separar lecturas de escrituras.

**Patrones solo donde aportan:**

- Strategy: fuentes de margen `export_json | odata`, proveedores de IA, y transporte de correo solo hasta que HU-003.5 confirme el único transporte real (los demás se retiran).
- Decorator: auditoría de herramientas, respaldo entre proveedores de IA.
- Repository + Unit of Work sobre EF Core.
- Options con `ValidateOnStart`.

**Convenciones de nombres:**

- Clases con prefijo `cls` (`clsCotizacionService`), incluidos records, clases estáticas, atributos (`clsSecretoAttribute`, usado como `[clsSecreto]`) y clases de prueba (`clsResultadoTests`). Interfaces con `ICls` (`IClsCotizacionRepository`).
- Structs, enums y delegados en PascalCase sin prefijo (`EstadoEnvio`).
- Miembros no privados y constantes en PascalCase; variables y parámetros en camelCase.
- Campos privados (de instancia y estáticos) en `_camelCase` (`_timeProvider`).
- Parámetros de tipo con `T` + PascalCase (`TValor`).
- Namespaces file-scoped (`namespace Cotifly.Dominio.Comun;`), exigidos con IDE0161 en `error`.
- Controllers: nunca `[Route("[controller]")]`, porque el prefijo `cls` terminaría en la URL; la ruta se escribe explícita según `specs/openapi.yaml` (ej. `[Route("health")]`).
- Tablas en PascalCase sin prefijo (ya creadas en `dbo_cotifly`) y columnas en camelCase. No se reintroduce `tbl`/`tblSap` (desviación del 2026-09-22, `docs/archivo/ESQUEMA_OBJETIVO_SQLSERVER_PARTE_B.md` §9).
- `.editorconfig` hace cumplir todas estas reglas (IDE1006 e IDE0161) con severidad `error`, y `Cotifly.Arquitectura.Tests` verifica `cls`/`ICls` sobre los ensamblados compilados. `Program` (instrucciones de nivel superior) queda exento: `Program.cs` suprime IDE1006 solo en su primera línea.

**Base técnica:** .NET 10, `Nullable` activado, `TreatWarningsAsErrors`, Central Package Management (`Directory.Packages.props`) con versiones fijas.

## Alternativas consideradas

- **Mantener dos servicios (API + servicio de herramientas):** lo contradice la decisión de HU-003 de absorber `mcp_server`, y agrega red y despliegue sin beneficio.
- **CQRS con MediatR:** agrega ceremonia sin una necesidad real de separar lecturas y escrituras.
- **Organizar por capas técnicas sin módulos:** va contra el escenario "Monolito Modular Moderno" de HU-003.

## Consecuencias

- La regla de dependencias se verifica automáticamente: un `using` de Infraestructura dentro de Aplicación rompe el build de tests.
- El prefijo `cls` choca con las convenciones habituales de .NET y de los analyzers. Se configura explícitamente en `.editorconfig` para que no genere advertencias falsas (con `TreatWarningsAsErrors` bloquearían el build).
- La serialización JSON se configura por grupo de endpoints, según el contrato #17, sin cambiar los nombres C#:
  - `/web/*` usa snake_case;
  - `/api/auth/*` usa camelCase (patrón "Login SSO — Cliente Pro").
  - Resuelto en #12 (parte B): `clsJsonSnakeCaseAttribute` marca el grupo (lo lleva `clsControladorWebBase`, base de todos los controllers de `/web/*`) y dos formateadores System.Text.Json snake_case (`clsFormateadorEntradaWeb`, `clsFormateadorSalidaWeb`), insertados al inicio de la lista de MVC, solo aceptan endpoints con esa marca. El resto (`/api/auth/*`, que usa nombres explícitos; `/health`; `/pdfs`) sigue con los formateadores por defecto. `clsContratoRutasTests` verifica que todo controller con ruta `web/...` tenga la marca y que cada DTO de `/web/*` produzca exactamente las propiedades snake_case de su esquema en `specs/openapi.yaml`.
