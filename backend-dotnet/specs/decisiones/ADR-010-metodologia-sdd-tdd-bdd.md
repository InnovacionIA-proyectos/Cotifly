# ADR-010 — Metodología: SDD, TDD y BDD

- **Estado:** Aceptada
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Aplica a todos los issues de HU-003 (#8–#16) y a HU-003.8 / HU-003.9.

## Contexto

La migración debe mantener la paridad de negocio con el Python: misma política de margen, mismo cálculo de precio, mismo PDF y mismo correo (HU-003, alcance punto 4). También debe respetar el contrato que consume el Angular de HU-004 (#17).

El Python tiene casos de prueba útiles que se pueden portar como datos: `tests/test_precio.py`, `tests/test_margen.py`, las fixtures de `tests/fixtures/{sap,cotizacion,chat}` y `datos/sap_export.json`. El estándar exige una estrategia de testing completa.

## Decisión

**1. Especificación primero (SDD).** Antes de escribir código de un módulo, deben existir:

- `backend-dotnet/specs/openapi.yaml`: el contrato HTTP congelado, fuente de verdad (#17; llega con el PR #53, aún no está en `QA`).
  - Hoy (#8) `Cotifly.Api.Tests` parsea el yaml (sin errores de diagnóstico) y valida las respuestas reales de los endpoints implementados contra su esquema (campos obligatorios, campos permitidos, enums).
  - La comparación contra el documento OpenAPI que genere la propia API queda pendiente para #14 (HU-003.8). **Resuelto en #14 (2026-09-25): no se compara contra un OpenAPI generado.** La deriva la cubren tres controles: el recorrido de rutas de `Cotifly.Api.Tests/Contrato/clsContratoRutasTests` (cada operación del contrato, `/api/auth/*`, `/web/*`, `/pdfs` y `/health*`, tiene su endpoint; ningún `/web/*` ni `/pdfs` queda fuera del contrato; todo `/web/*` y `/pdfs` responde 401 sin token), la validación de los cuerpos reales contra su esquema y `redocly lint` en el CI. `/api/auth/*` es anónimo y queda fuera del recorrido de 401: lo validan `clsAuthEndpointTests` y `login_clientepro.feature`/`refresh_logout.feature` por HTTP contra SQL Server real (`LoginResponse`, `UsuarioSesion` y `ErrorWeb` contra el esquema, 400/401/429, logout 204). `/chat/events` (webhook de Google Chat) se retiró en HU-013.9 (#105) con `clsChatEventosEndpointTests` y `google_chat.feature`; `clsRetiroGoogleChatTests` verifica que responde 404.
- `backend-dotnet/specs/features/*.feature`: los escenarios Gherkin del backlog, más los nuevos de este diseño (`chat_slot_filling`, `aislamiento_comercial`, `login_clientepro`, `refresh_logout`, entre otros).
- `backend-dotnet/specs/decisiones/`: estas ADR.

**2. TDD en cada módulo:** test en rojo, implementación, refactor. Los casos del Python se portan como `Theory` con los mismos datos de entrada y salida esperada.

**3. Pirámide de tests:**

| Nivel | Proyecto | Herramientas | Alcance |
|---|---|---|---|
| Unitario | `Cotifly.Dominio.Tests`, `Cotifly.Aplicacion.Tests` | xUnit, NSubstitute, AwesomeAssertions | Reglas puras (precio, margen, folio), casos de uso con puertos simulados, runner del agente con un `IChatClient` guionizado |
| Integración | `Cotifly.Infraestructura.Tests` | Testcontainers.MsSql | Repositorios, scripts `db/migrations_mssql/` aplicados, esquema igual al modelo EF |
| API | `Cotifly.Api.Tests` | `WebApplicationFactory` | Forma exacta de las respuestas del contrato, auth, CORS, aislamiento por comercial |
| Aceptación | `Cotifly.Aceptacion.Tests` | Reqnroll (BDD) con `Reqnroll.xunit.v3` | Los `.feature` de `specs/features` |
| Arquitectura | `Cotifly.Arquitectura.Tests` | ArchUnitNET (alternativa: NetArchTest.eNhancedEdition) | Reglas de dependencia entre capas, Dominio sin paquetes externos y convención `cls`/`ICls` (ADR-002) |

Metas:

- 100 % de los casos Python portados en precio y margen, y cobertura de Dominio de al menos 90 %.
- Cero advertencias de build (`TreatWarningsAsErrors`).
- Las pruebas de integración llevan `[Trait("Categoria","Integracion")]`.
- Las pruebas de arquitectura van en su propio proyecto, `Cotifly.Arquitectura.Tests`, y no en `Cotifly.Aplicacion.Tests`: tienen que cargar los ensamblados de las 4 capas, y ponerlas en Aplicacion.Tests obligaría a ese proyecto a referenciar Infraestructura y Api (decidido en #8).
- **Runner:** xUnit v3 sobre Microsoft.Testing.Platform (MTP). `backend-dotnet/global.json` activa MTP para `dotnet test`; el SDK 10 ya no acepta el modo VSTest con xUnit v3.
  - Filtros con opciones de MTP, por ejemplo `dotnet test --filter-not-trait "Categoria=Integracion"` (excluir integración) o `--filter-trait "Categoria=Integracion"`.
  - Pendiente para CI (#14): agregar `Microsoft.Testing.Extensions.TrxReport` (reporte TRX) y `Microsoft.Testing.Extensions.CodeCoverage` (cobertura). No se agregan antes porque todavía no se usan.
  - Los filtros por trait dejan proyectos con cero pruebas, y MTP devuelve el código 8 en ese caso; las corridas filtradas agregan `--ignore-exit-code 8` (decidido en #9).
- **BDD con xUnit v3 (decidido en #9):** se usa `Reqnroll.xunit.v3` (Reqnroll 3.x), que genera pruebas xUnit v3 y corre sobre MTP igual que el resto. Se descartó `Reqnroll.MSTest` solo para aceptación: habría mezclado dos frameworks de prueba sin necesidad. Los `.feature` quedan en `specs/features/` y el proyecto de aceptación los enlaza (code-behind en `obj/`).

**4. Build y test dentro de Docker.** La máquina de desarrollo solo tiene SDK .NET 8, así que todo se compila y se prueba con `mcr.microsoft.com/dotnet/sdk:10.0` (ADR-007). CI repite los mismos pasos: restore, build, test y `docker build`.

**5. Paridad y QA:**

- Paridad automatizada en precio y margen (tests unitarios con datos del Python).
- Paridad visual del PDF (3 PDFs comparados en #11).
- Paridad funcional con al menos 10 casos reales que ejecuta QA (HU-003.9).

**6. Redondeo con paridad al Python (Aceptada).**

- Python redondea con `round()`, que usa redondeo bancario (half-to-even).
- En .NET se usa `Math.Round(valor, MidpointRounding.ToEven)` sobre `decimal`, nunca sobre `double`.
- Aplica al precio en pesos y a la utilidad con 2 decimales.
- Hay un test explícito para los casos de punto medio.
- **Paridad con Python salvo empates exactos de medio peso** (decidido en #10): .NET redondea el valor exacto (half-to-even) y Python arrastra el error de `float`; diferencia máxima 1 peso (~0,04 % de casos). Ejemplos documentados en `clsCalculadoraPrecioTests` y `precio.feature`: 55 con markup 0.1 = 60.5 → **60** (Python 61, porque 55 × 1.1 = 60.50000000000001); 6349 con margen 0.44 = 11337.5 → **11338** (Python 11337, porque calcula 11337.499999999998). Se prefiere el valor exacto; el redondeo queda por confirmar con negocio.

**7. Criterio de licencias.**

- Solo se usan dependencias con licencia libre para uso corporativo (MIT, Apache-2.0, BSD, SIL OFL).
- No se usan dependencias con licencia comercial condicionada a ingresos o de pago. Es el mismo criterio que descartó QuestPDF en ADR-006.
- Por eso las aserciones usan **AwesomeAssertions**, fork Apache-2.0 de FluentAssertions. FluentAssertions 8 pasó a licencia comercial paga de Xceed.
- **Excepción (#14): herramientas de prueba de Microsoft con licencia gratuita, no distribuidas.** Se admite `Microsoft.Testing.Extensions.CodeCoverage` (Microsoft Software License Terms, no OSI) porque es gratuita, solo corre en las pruebas y el CI, y no viaja en la imagen ni en el código entregado. Su paquete declara `requireLicenseAcceptance`: al restaurarlo se aceptan esos términos. La licencia incluye una cláusula de recolección de datos (4.a: el software puede enviar telemetría de uso a Microsoft); en el CI se fijan `DOTNET_CLI_TELEMETRY_OPTOUT` y `TESTINGPLATFORM_TELEMETRY_OPTOUT` (la licencia advierte que no todos los escenarios admiten exclusión, así que queda para revisar con seguridad de la información). La excepción no se extiende a dependencias de producción.

## Implementación en CI (#14, HU-003.8, 2026-09-25)

- `Microsoft.Testing.Extensions.TrxReport` 2.4.0 (MIT) y `Microsoft.Testing.Extensions.CodeCoverage` 18.11.2 se agregaron a CPM y a todos los proyectos de prueba (`tests/Directory.Build.props`). CodeCoverage tiene licencia gratuita de Microsoft (no MIT): se acepta como excepción acotada al punto 7 porque este ADR ya la preveía, solo corre en las pruebas y no se distribuye en la imagen.
- Cobertura solo de los ensamblados de `src/` (`tests/cobertura.config.xml`), unida con ReportGenerator. **Umbral obligatorio: `Cotifly.Dominio` ≥ 90 % de líneas** (el job `cobertura` falla por debajo); las demás capas solo se reportan. Medido el 2026-09-25: Dominio 99,3 %, total 97,5 % de líneas.
- La comparación contra un documento OpenAPI generado por la propia API (punto 1) no se implementa: el recorrido de rutas de `Cotifly.Api.Tests` (toda operación del contrato expuesta, ningún `/web/*` fuera del contrato) y la validación de cada respuesta contra su esquema cubren la deriva sin agregar un generador de OpenAPI. El CI suma `redocly lint`.
- Detalle en `backend-dotnet/docs/08-testing.md` §8.6 y §8.7.

## Alternativas consideradas

- **Código primero, tests después:** con paridad obligatoria, el riesgo es descubrir las diferencias tarde, en QA.
- **Solo tests de extremo a extremo:** son lentos y frágiles, y no aíslan en qué capa está la diferencia.
- **SpecFlow en lugar de Reqnroll:** SpecFlow está descontinuado. Reqnroll es su continuación compatible.
- **FluentAssertions 8:** licencia comercial de Xceed. Descartada.
- **FluentAssertions 7.x con versión fija (última Apache-2.0):** es viable, pero queda congelada sin evolución.
- **Shouldly:** también es viable, con una API distinta. AwesomeAssertions se elige por mantener la API de FluentAssertions.
- **NetArchTest.Rules:** no tiene mantenimiento desde 2021.

## Consecuencias

- Cualquier cambio de contrato pasa primero por `openapi.yaml` y su revisión (#17). Hasta entonces, el test de contrato falla.
- Cada PR de módulo incluye sus tests y, cuando aplica, su `.feature`. El agente validador del flujo de trabajo los ejecuta dentro de Docker antes de la auditoría.
- Los tests del agente no llaman a proveedores de IA reales. Las pruebas con proveedores reales (al menos dos) son manuales y quedan documentadas en HU-003.6.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Set de al menos 10 casos reales de paridad (HU-003.9) | Kevin |
| Runner de CI con Docker (ver ADR-007) | Duban |
