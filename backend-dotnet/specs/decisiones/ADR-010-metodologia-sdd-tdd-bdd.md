# ADR-010 — Metodología: SDD, TDD y BDD

- **Estado:** Aceptada
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Aplica a todos los issues de HU-003 (#8–#16) y a HU-003.8 / HU-003.9.

## Contexto

La migración debe mantener la paridad de negocio con el Python: misma política de margen, mismo cálculo de precio, mismo PDF y mismo correo (HU-003, alcance punto 4). También debe respetar el contrato que consume el Angular de HU-004 (#17).

El Python tiene casos de prueba útiles que se pueden portar como datos: `tests/test_precio.py`, `tests/test_margen.py`, las fixtures de `tests/fixtures/{sap,cotizacion,chat}` y `datos/sap_export.json`. El estándar exige una estrategia de testing completa.

## Decisión

**1. Especificación primero (SDD).** Antes de escribir código de un módulo, deben existir:

- `backend-dotnet/specs/openapi.yaml`: el contrato HTTP congelado, fuente de verdad (#17; llega con el PR #53, aún no está en `QA`). Un test compara el documento OpenAPI que genera la API contra este archivo.
- `backend-dotnet/specs/features/*.feature`: los escenarios Gherkin del backlog, más los nuevos de este diseño (`chat_slot_filling`, `aislamiento_comercial`, `login_clientepro`, `refresh_logout`, entre otros).
- `backend-dotnet/specs/decisiones/`: estas ADR.

**2. TDD en cada módulo:** test en rojo, implementación, refactor. Los casos del Python se portan como `Theory` con los mismos datos de entrada y salida esperada.

**3. Pirámide de tests:**

| Nivel | Proyecto | Herramientas | Alcance |
|---|---|---|---|
| Unitario | `Cotifly.Dominio.Tests`, `Cotifly.Aplicacion.Tests` | xUnit, NSubstitute, AwesomeAssertions | Reglas puras (precio, margen, folio), casos de uso con puertos simulados, runner del agente con un `IChatClient` guionizado |
| Integración | `Cotifly.Infraestructura.Tests` | Testcontainers.MsSql | Repositorios, scripts `migrations_mssql/` aplicados, esquema igual al modelo EF |
| API | `Cotifly.Api.Tests` | `WebApplicationFactory` | Forma exacta de las respuestas del contrato, auth, CORS, aislamiento por comercial |
| Aceptación | `Cotifly.Aceptacion.Tests` | Reqnroll (BDD) | Los `.feature` de `specs/features` |
| Arquitectura | en `Cotifly.Aplicacion.Tests` | ArchUnitNET (alternativa: NetArchTest.eNhancedEdition) | Reglas de dependencia entre capas (ADR-002) |

Metas:

- 100 % de los casos Python portados en precio y margen, y cobertura de Dominio de al menos 90 %.
- Cero advertencias de build (`TreatWarningsAsErrors`).
- Las pruebas de integración llevan `[Trait("Categoria","Integracion")]`.

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

**7. Criterio de licencias.**

- Solo se usan dependencias con licencia libre para uso corporativo (MIT, Apache-2.0, BSD, SIL OFL).
- No se usan dependencias con licencia comercial condicionada a ingresos o de pago. Es el mismo criterio que descartó QuestPDF en ADR-006.
- Por eso las aserciones usan **AwesomeAssertions**, fork Apache-2.0 de FluentAssertions. FluentAssertions 8 pasó a licencia comercial paga de Xceed.

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
