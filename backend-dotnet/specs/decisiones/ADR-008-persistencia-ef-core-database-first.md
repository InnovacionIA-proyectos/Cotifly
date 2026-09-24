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

## Pendientes

| Pendiente | Dueño |
|---|---|
| Crear `cotifly_app` con esos permisos y entregar las credenciales por canal seguro | DBA (lo solicita Duban) |
| Merge del PR #40 (script 0001) a `QA` | scalderon-bs |
| Ratificar "solo scripts `.sql`" y revisar y aplicar `0002_identidad_estado_agente.sql` | Sara + scalderon-bs |
| Acceso de red y permisos a `DWCubosEBSSAP` desde el contenedor | DBA |
