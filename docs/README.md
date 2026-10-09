# Documentación de Cotifly

Los documentos están separados por tema. La base de datos en sí (scripts, seeders y ejecutador) está en `db/`, no aquí; en `docs/database/` solo está la documentación de la base.

| Carpeta | Qué tiene |
|---|---|
| [`backlog/`](backlog/) | Las historias de usuario y los pendientes, **cada archivo con la fecha en que se creó** al inicio del nombre (`AAAA-MM-DD_...`). |
| [`database/`](database/) | Diseño de la base de datos: diccionario, esquema objetivo en SQL Server, la propuesta de refactor original (HU-002.1), la propuesta de reestructuración de cotizaciones (HU-021) y los cambios del backend que esta provoca. |
| [`conexion_nexo/`](conexion_nexo/) | Todo lo que tiene que ver con Nexo: mapeo de campos, formato del Excel de licitaciones, preguntas pendientes para Nexo y los próximos alcances de la integración. Incluye `2026-10-08_CONSIDERACIONES_PARA_NEXO.md`: lo que Cotifly le pide a Nexo y al cubicador por la reestructuración de cotizaciones. |
| [`docs_front/`](docs_front/) | Lo que consume el frontend: contrato de la API web, guía del frontend y el manual de conexión con ClientePro (PDF). |
| [`infra/`](infra/) | Diagnósticos de infraestructura. |

## `backlog/` por fecha

| Fecha | Documento | Qué es |
|---|---|---|
| 2026-09-21 | `2026-09-21_HU_Backlog_Cotifly_ClientePro.md` | El backlog original: HU-001 a HU-013 |
| 2026-09-29 | `2026-09-29_PENDIENTES_CORRECCION_DUBAN.md` | Hallazgos de las pruebas en vivo para Duban. **Git lo ignora a propósito** (no viaja en los commits) |
| 2026-10-07 | `2026-10-07_HU_Sprint5_Cotifly.md` | Sprint 5: HU-014 a HU-020 |
| 2026-10-08 | `2026-10-08_HU_Correccion_BD_Cotifly.md` | **[Corrección con BD]**, HU-021: se hace antes del Sprint 5 |

Un documento nuevo de HU lleva en `backlog/` la fecha de su creación como prefijo, y se agrega a esta tabla.

Los documentos de `backend-dotnet/` (arquitectura, ADR, specs) viven con el código y no se movieron.
