# ADR-008 — Persistencia: EF Core Database First sobre `dbo_cotifly`

- **Estado:** Aceptada (desviación del Gherkin de HU-003; el login de servicio está pendiente con el DBA)
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Se implementa en #9 (HU-003.3). Relacionada con #5 (HU-002.4) y #44.

## Contexto

El esquema `dbo_cotifly` ya existe en `dbTarifario_Nexo_Pruebas` (host `tbsdatacenter`). Se creó con T-SQL directo usando `migrations_mssql/0001_script_bd_cotifly_sqlserver.sql` (PR #40, HU-002.4, todavía no está en `QA`).

- HU-003.3 aclara que no deben generarse migraciones EF que recreen el esquema, ni mezclarse los dos caminos.
- El Gherkin general de HU-003 pide "migraciones EF Core versionadas equivalentes a las que hoy provee Alembic".
- El backend corre en un contenedor Linux, sin autenticación integrada de Windows.
- Además de su base, necesita leer `dbsolucionesti.tblSAPUsuariosApp` (identidad para el login, HU-003.6) y `DWCubosEBSSAP.DimEjecutivo` (regional del comercial).

## Decisión

1. **EF Core Database First.** `clsCotiflyDbContext` usa `HasDefaultSchema("dbo_cotifly")` y `UseSqlServer(..., o => o.UseCompatibilityLevel(150))`, que corresponde a SQL Server 2019 y evita generar SQL que no existe en esa versión. El mapeo es Fluent API a mano contra las tablas reales:
   - tablas en PascalCase y columnas en camelCase explícitas;
   - PK compuesta en `Sesiones` y FK compuesta en `EventosConversacion`;
   - `UsuariosWeb.id` como `UNIQUEIDENTIFIER` generado por Cotifly;
   - JSON guardado como `string`;
   - `updatedAt` lo asigna un interceptor, porque el script no crea triggers.
2. **Cambios de esquema solo con scripts `migrations_mssql/000N_*.sql`**, idempotentes y revisados por el equipo de datos. No se usan migraciones EF.
   - Un test aplica todos los scripts en Testcontainers y verifica que el modelo EF coincide con el esquema resultante.
   - Siguiente script: `0002_identidad_estado_agente.sql` (ADR-004, ADR-009). Ya existió un `0002_seed_configuracion.sql` que después se consolidó en 0001 (backlog, HU-002.4); por eso el nombre nuevo es explícito para no confundirlos.
   - **Desviación explícita** del Gherkin de HU-003 ("migraciones EF Core versionadas"), del mismo tipo que ADR-007 con compose. Queda pendiente la ratificación de Sara (HU-003.3).
3. **Tres conexiones, cada una con su propio `DbContext`:**
   - `ConnectionStrings:Cotifly`: lectura y escritura sobre `dbo_cotifly`.
   - `ConnectionStrings:Identidad`: solo lectura a `dbsolucionesti`. Usa la entidad sin clave `clsUsuarioSapApp {Id, NombreUsuario, DescripcionUsuario, Email}`; `CLAVE` nunca se mapea.
   - `ConnectionStrings:Regional`: solo lectura a `DWCubosEBSSAP.DimEjecutivo`.

   Los contextos de solo lectura usan `NoTracking` y sobrescriben `SaveChanges` / `SaveChangesAsync` para lanzar una excepción. Lo que garantiza la solo lectura son los permisos del login. (`ApplicationIntent=ReadOnly` no se usa: solo redirige a réplicas de lectura en grupos de disponibilidad y no impide escribir.)
4. **Login SQL de servicio `cotifly_app`** con permisos mínimos:
   - DML sobre `dbo_cotifly`;
   - `SELECT` sobre `tblSAPUsuariosApp` sin `CLAVE` (vía vista o permiso por columna);
   - `SELECT` sobre `DimEjecutivo`.

   Todos los secretos (contraseña SQL, SAP OData, Grovity, API key del LLM, `Jwt:SigningKey`) llegan por Dokploy (HU-006.5). En `Production`, el arranque falla si falta alguno, si queda un valor por defecto o si la cadena usa `Trusted_Connection`.
   - El mecanismo de fallo al arrancar del backend .NET lo da el patrón de configuración de #8 (HU-003.2): opciones con DataAnnotations + `ValidateOnStart`, atributo `[clsSecreto]` y `clsValidadorSecretos` (vacío, placeholder o largo mínimo en `Production`).
   - Las validaciones concretas llegan con cada HU: `ConnectionStrings` y la regla de `Trusted_Connection` en #9; `Jwt`, `IA` y `GChat` en #12.
   - El issue #1 corresponde al backend Python, no a este.

## Alternativas consideradas

- **Code First con migraciones EF:** chocaría con el esquema creado por el 0001.
- **Adoptar migraciones EF ahora, con una baseline vacía:** es posible más adelante si datos lo decide (HU-003.3). Por ahora se mantiene el patrón de scripts `.sql` ya establecido.
- **Windows Auth / Kerberos en Linux:** exige keytab y configuración de dominio en la imagen. Es desproporcionado.
- **Dapper sin EF:** se pierden el change tracking y el Unit of Work, y el estándar asume EF Core.

## Consecuencias

- La trazabilidad de versiones del esquema la dan los scripts numerados más el test que compara modelo y esquema, no el historial de migraciones EF.
- Mientras no exista `cotifly_app`, el desarrollo y los tests usan Testcontainers. El smoke contra la base real (diferido de HU-002.4) espera al login.
- **Issue #44** (fallo de FK `fk_cotizaciones_linea_negocio` en `generar_pdf`):
  - En SQL Server esa FK no existe. Solo existe `fk_cotizaciones_lineaNegocioId` (por id, columna nullable) y no hay columna de texto `lineaNegocio`. El bug desaparece con la migración.
  - Además, en .NET la línea se resuelve contra `MargenesPolitica` de forma determinista (sin distinguir tildes ni mayúsculas) antes de renderizar. Si no calza, se responde `MARGEN_LINEA_DESCONOCIDA` sin perder trabajo.

## Implementación (#9, HU-003.3)

Lo decidido arriba se implementó así; donde difiere, manda esta sección:

- `clsCotiflyDbContext` vive en `Infraestructura/Comun/Persistence/` con una configuración Fluent API por tabla en `Configurations/` (tipo SQL explícito en cada columna, nombres de PK/FK/índices del script, CHECK reflejados). Enums guardados con su código snake_case (`clsConvertidorCodigoEnum`); `Sesiones.estadoAgente` como JSON de `clsEstadoConversacion`.
- `clsMarcasTiempoInterceptor` fija `updatedAt` en filas nuevas y modificadas y también `createdAt` en filas nuevas cuando la entidad no lo trae, siempre con `IClsReloj` (así las reglas con ventana de tiempo, como el dedupe de correo de 5 minutos, se miden con el mismo reloj que escribió la fila).
- `IClsUnitOfWork` traduce las violaciones de unicidad (SQL 2627/2601) a `clsViolacionUnicidadException` con código de negocio (`uq_cotizaciones_folio` → `FOLIO_DUPLICADO`).
- **Cambio frente a la decisión:** `clsUsuarioSapApp` se mapea **con clave** (`ID`), no como entidad sin clave: permite `Find`/búsquedas por ID (respaldo del login, ADR-009) y sigue siendo de solo lectura (`NoTracking` + `SaveChanges` lanza). Vive en `Dominio/Identidad` y `clsIdentidadDbContext` en `Infraestructura/Identidad/Persistence/`.
- El esquema y la tabla de `dbsolucionesti` son configurables (`Identidad:Esquema` = `dbo`, `Identidad:Tabla` = `tblSAPUsuariosApp`) hasta que el DBA confirme la ubicación real.
- `ConnectionStrings:Regional` / `DWCubosEBSSAP` queda para #12, donde se usa.
- Validación al arrancar en Production: `[clsSecreto]` sobre `ConnectionStrings:Cotifly` e `Identidad` más `clsValidadorOpcionesConexiones` (rechaza `Trusted_Connection` / `Integrated Security` y contraseñas placeholder).
- `migrations_mssql/0002_identidad_estado_agente.sql` agrega `Sesiones.estadoAgente` (CHECK `ISJSON`) y `Sesiones.usuarioId` (FK), las tablas `RolesMapeo`, `TokensRefresco` (hash `CHAR(64)` único con collation binaria, `familiaId`, `reemplazadoPorId`) y `TokensRevocados`, e índices de consulta. Es una **extensión aditiva pendiente de aprobación**, idempotente y no destructiva, y un **paso de un solo sentido**: re-ejecutar el 0001 después del 0002 destruye las vistas y 6 tablas (CorreoDestinatarios, MargenAjustes, Correos, EventosConversacion, Cotizaciones, Sesiones) antes de fallar en `DROP TABLE UsuariosWeb` (con `sqlcmd -b`); sin `-b` (SSMS) sigue y recrea Sesiones sin `estadoAgente`/`usuarioId` y sin los índices del 0002. Se propone al PR #40 una guarda al inicio del paso 0bis del 0001: `IF OBJECT_ID(N'dbo_cotifly.TokensRefresco','U') IS NOT NULL THROW 50002, '0002 ya aplicado: el 0001 no debe re-ejecutarse', 1;` (el 0001 no se modifica en #9).
- La prueba de deriva (`clsEsquemaTests`) aplica 0001 + 0002 en SQL Server 2019 (Testcontainers) y compara en ambos sentidos el modelo EF (modelo de diseño) con el catálogo: tablas, columnas, tipo SQL, nulabilidad y collation (`INFORMATION_SCHEMA.COLUMNS`); nombres y columnas de PK y FK (`sys.key_constraints`, `sys.foreign_keys`); índices y restricciones únicas con orden, `DESC`, `INCLUDE` y filtro (`sys.indexes`); nombres de CHECK (`sys.check_constraints`; la expresión no se compara porque SQL Server la reescribe). La idempotencia del 0002 se verifica con una foto completa del esquema (columnas, defaults, claves, FK, índices, CHECK) y el conteo de filas antes y después de re-aplicarlo. Para que la comparación de índices sea exacta, el modelo quita la convención de EF que crea un índice por FK (`ForeignKeyIndexConvention`).
- `GET /health/ready` usa `clsBaseDatosHealthCheck`: conecta (`CanConnectAsync`) y verifica que el 0002 está aplicado (`Sesiones.estadoAgente` y `TokensRefresco`); si falta responde 503 con el detalle fijo `esquema incompleto: falta 0002`. La sonda completa tiene un límite de 5 s para no colgar el endpoint los 15 s del timeout de conexión por defecto.

### Deuda técnica anotada (no se mueve código en #9)

| Deuda | Dónde se resuelve |
|---|---|
| ~~`IClsCotizacionRepository.MarcarSeguimientoAsync` e `IClsCorreoRepository.VentanaDuplicado` contienen reglas de negocio en puertos de repositorio (se portaron así desde `shared/repos.py`): pasar a casos de uso~~ | **Resuelto en #11:** `IClsSeguimientoService` (dueño, ganada/motivo ≤ 2000, `marcadoAt` con `IClsReloj`) y `clsCorreoService` (ventana de 5 min y comparación de destinatarios); el repositorio de correos solo expone consultas (`ListarEnviadosDesdeAsync`, `ObtenerUltimoEnviadoAClienteAsync`) |
| `clsUsuarioSapApp` (Dominio) pasa a Aplicacion detrás de un puerto de directorio corporativo | #12 |
| `clsIdentidadDbContext` solo bloquea `SaveChanges`; `ExecuteUpdate`/`ExecuteDelete`/`ExecuteSqlRaw` no pasan por ahí. La garantía real son los permisos de `cotifly_app` (sin UPDATE/DELETE, sin SELECT sobre `CLAVE`) | DBA |
| `IClsSesionRepository.TocarAsync` crea la sesión si no existe y sin dueño: #12 no debe usarlo para crear sesiones web (usar `ObtenerOCrearAsync` con `usuarioId`) | #12 |
| ~~El dedupe de correo (`ExisteDuplicadoRecienteAsync`) no toma lock: dos envíos simultáneos pueden pasar ambos (igual que el Python)~~ | **Resuelto en parte en #11:** `clsCandadoEnvios` serializa los envíos de una cotización dentro del proceso. Con varias réplicas hace falta un candado en la base (`sp_getapplock`): HU-006.6, si se escala |
| Columnas `NVARCHAR(MAX)` que habrá que acotar e indexar en un script 0003: `Cotizaciones.nitCliente`, `UsuariosWeb.email`, `Cotizaciones.estadoCosteo`, `Cotizaciones.costeoId` | 0003 (datos) |
| `folio_versionado` / nueva versión y búsqueda de cotizaciones por cliente | #12 (ADR-011) |
| Vistas de métricas (`vAjustesMargenPorComercial`, `vActividadPorComercial`) como entidades sin clave | #45 |
| `vActividadPorComercial` cuenta los envíos con `estadoEnvio = 'enviada'`; desde #11 un reenvío deja `reenviada`, así que la vista subcuenta. Script 0003 con `estadoEnvio IN ('enviada','reenviada')` (el contrato `ActividadComercial` ya lo pide) | 0003 (Sara) + #45 |
| Métodos de transición del costeo Nexo (`estadoCosteo`, `costeoListoAt`, `ganada` definitivo) | HU-005 |
| ~~Resolución de línea de negocio sin tildes: la collation `CI_AS` distingue acentos (`Distribucion` ≠ `Distribución`)~~ | **Resuelto en #10:** `clsMargenResolverService` compara en memoria contra las líneas de `MargenesPolitica` con `clsReglasMargen.NormalizarLinea` (sin tildes, mayúsculas ni espacios) y consulta el mapeo con el nombre canónico. El repositorio sigue comparando con la collation |
| ~~`clsMargenAjuste` sigue exigiendo márgenes en [0, 1) porque no conoce el método de cálculo; con `markup` un ajuste ≥ 1 se rechazaría~~ | **Resuelto en #10:** `clsMargenAjuste.Registrar` recibe `metodoCalculo` y valida con `clsReglaMargen` |
| `MargenAjustes.margenPolitica`, `margenAplicado` y `diferencia` son `DECIMAL(9,4)`: el Python redondeaba la diferencia a 6 decimales; .NET redondea los dos márgenes a 4 (half-to-even) antes de calcular la diferencia, así lo guardado cuadra con lo devuelto. Si negocio necesita más precisión, ampliar la escala en un script 0003 | 0003 (datos), si aplica |

## Pendientes

| Pendiente | Dueño |
|---|---|
| Crear `cotifly_app` con esos permisos y entregar las credenciales por canal seguro | DBA (lo solicita Duban) |
| Confirmar esquema real de `tblSAPUsuariosApp` en `dbsolucionesti` (hoy se asume `dbo`, configurable) | DBA |
| Smoke del `DbContext` contra `tbsdatacenter` (diferido de HU-002.4; espera a `cotifly_app`) | Duban |
| Merge del PR #40 (script 0001) a `QA` | scalderon-bs |
| Ratificar "solo scripts `.sql`" y revisar y aplicar `0002_identidad_estado_agente.sql` | Sara + scalderon-bs |
| Acceso de red y permisos a `DWCubosEBSSAP` desde el contenedor | DBA |
