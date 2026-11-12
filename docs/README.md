# Documentación de Cotifly

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-19

Índice único de la documentación del proyecto. Cada tema tiene **un solo documento de referencia**; los demás lo
enlazan en vez de repetirlo. El punto de entrada general es el [`README.md`](../README.md) de la raíz.

## Qué leer para qué

| Si necesitas… | Lee |
|---|---|
| Integrar el frontend (pantallas, flujos, estados y botones) | [`docs_front/GUIA_FRONTEND_COTIFLY.md`](docs_front/GUIA_FRONTEND_COTIFLY.md) |
| El contrato de cada endpoint para el frontend | [`docs_front/CONTRATOS_API_COTIFLY_FRONTEND.md`](docs_front/CONTRATOS_API_COTIFLY_FRONTEND.md) y, para el contrato por rutas con ejemplos verificados, [`docs_front/CONTRATO_API_WEB.md`](docs_front/CONTRATO_API_WEB.md). Fuente de verdad: [`openapi.yaml`](../backend-dotnet/specs/openapi.yaml) |
| Entender el sistema y su arquitectura | [`backend-dotnet/docs/01-descripcion.md`](../backend-dotnet/docs/01-descripcion.md) y [`02-arquitectura.md`](../backend-dotnet/docs/02-arquitectura.md) |
| Levantar el backend en local y correr las pruebas | [`backend-dotnet/docs/06-ejecucion-local.md`](../backend-dotnet/docs/06-ejecucion-local.md) y [`08-testing.md`](../backend-dotnet/docs/08-testing.md) |
| Desplegar (Docker Swarm y Dokploy) | [`DEPLOY.md`](../DEPLOY.md) y [`backend-dotnet/docs/03-infraestructura.md`](../backend-dotnet/docs/03-infraestructura.md) (variables y configuración) |
| Operar y dar soporte | [`backend-dotnet/docs/10-operacion.md`](../backend-dotnet/docs/10-operacion.md) |
| Conocer una decisión de arquitectura | [`backend-dotnet/specs/decisiones/README.md`](../backend-dotnet/specs/decisiones/README.md) (índice de ADR) |
| Trabajar con la base de datos | [`BASE_DE_DATOS.md`](../BASE_DE_DATOS.md), [`database/DICCIONARIO_BASE_DE_DATOS.md`](database/DICCIONARIO_BASE_DE_DATOS.md) y los scripts en [`db/README.md`](../db/README.md) |
| Integrarse con Nexo o con el cubicador | [`backend-dotnet/docs/COTIFLY-CONTRATO.md`](../backend-dotnet/docs/COTIFLY-CONTRATO.md) y [`backend-dotnet/docs/COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md`](../backend-dotnet/docs/COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md) |
| Escribir código o comentarios nuevos | Convenciones en [`backend-dotnet/docs/02-arquitectura.md`](../backend-dotnet/docs/02-arquitectura.md#24-convenciones-de-nombres) (nombres y comentarios) |

## Por tema

| Tema | Documento de referencia | Complementos |
|---|---|---|
| Qué es Cotifly, roles y capacidades | [`backend-dotnet/docs/01-descripcion.md`](../backend-dotnet/docs/01-descripcion.md) | [`backend-dotnet/README.md`](../backend-dotnet/README.md) |
| Arquitectura del backend | [`backend-dotnet/docs/02-arquitectura.md`](../backend-dotnet/docs/02-arquitectura.md) | [`04-flujo-del-sistema.md`](../backend-dotnet/docs/04-flujo-del-sistema.md), [`05-stack.md`](../backend-dotnet/docs/05-stack.md), [`09-estructura-del-proyecto.md`](../backend-dotnet/docs/09-estructura-del-proyecto.md) |
| Decisiones de arquitectura (ADR) | [`backend-dotnet/specs/decisiones/README.md`](../backend-dotnet/specs/decisiones/README.md) | ADR-001 a ADR-032 |
| Contrato HTTP (fuente de verdad) | [`backend-dotnet/specs/openapi.yaml`](../backend-dotnet/specs/openapi.yaml) | Escenarios BDD en [`backend-dotnet/specs/features/`](../backend-dotnet/specs/features/), resumen en [`07-api.md`](../backend-dotnet/docs/07-api.md) |
| API para el frontend (ClientePro) | [`docs_front/CONTRATO_API_WEB.md`](docs_front/CONTRATO_API_WEB.md) | [`docs_front/GUIA_FRONTEND_COTIFLY.md`](docs_front/GUIA_FRONTEND_COTIFLY.md) (flujos, estados y reglas de UI), [`docs_front/CONTRATOS_API_COTIFLY_FRONTEND.md`](docs_front/CONTRATOS_API_COTIFLY_FRONTEND.md) (referencia consolidada de los 46 endpoints, errores y checklist de integración) y el [manual de conexión con ClientePro](<docs_front/Manual de Conexión ClientePro - Grovity.pdf>) (PDF) |
| Integración con Nexo | [`backend-dotnet/docs/COTIFLY-CONTRATO.md`](../backend-dotnet/docs/COTIFLY-CONTRATO.md) (contrato de captura que publicó Nexo) | [`conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md`](conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md), [`conexion_nexo/HU-013-preguntas-nexo.md`](conexion_nexo/HU-013-preguntas-nexo.md), [ADR-015](../backend-dotnet/specs/decisiones/ADR-015-costeo-nexo.md) |
| Integración con el cubicador | [`backend-dotnet/docs/COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md`](../backend-dotnet/docs/COTIFLY-COMO-CONECTARSE-AL-CUBICADOR.md) (guía que publicó el cubicador) | [ADR-020](../backend-dotnet/specs/decisiones/ADR-020-conexion-con-el-cubicador.md) |
| Peticiones abiertas a Nexo y al cubicador | [`conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md`](conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md) | [`conexion_nexo/PROXIMOS_ALCANCES.md`](conexion_nexo/PROXIMOS_ALCANCES.md) |
| Licitaciones por Excel | [`conexion_nexo/HU-011-formato-excel-licitacion.md`](conexion_nexo/HU-011-formato-excel-licitacion.md) | [ADR-017](../backend-dotnet/specs/decisiones/ADR-017-licitaciones-multilinea.md) |
| Base de datos | [`BASE_DE_DATOS.md`](../BASE_DE_DATOS.md) | [`database/ESQUEMA_OBJETIVO_SQLSERVER.md`](database/ESQUEMA_OBJETIVO_SQLSERVER.md) (esquema), [`database/DICCIONARIO_BASE_DE_DATOS.md`](database/DICCIONARIO_BASE_DE_DATOS.md) (columnas), [`db/README.md`](../db/README.md) (scripts) |
| Historia del esquema de HU-021 | [`database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md`](database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md) (ejecutada) | [`database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md`](database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md) (ejecutado), [ADR-019](../backend-dotnet/specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md) |
| Ejecución local | [`backend-dotnet/docs/06-ejecucion-local.md`](../backend-dotnet/docs/06-ejecucion-local.md) | Scripts `backend-dotnet/scripts/levantar-local.*` |
| Configuración e infraestructura del backend | [`backend-dotnet/docs/03-infraestructura.md`](../backend-dotnet/docs/03-infraestructura.md) | `backend-dotnet/.env.example` |
| Despliegue (Docker Swarm y Dokploy) | [`DEPLOY.md`](../DEPLOY.md) | Manifiestos en [`infra/`](../infra/), diagnóstico TLS en [`infra/HU-006.1-diagnostico-tls.md`](infra/HU-006.1-diagnostico-tls.md) |
| Operación y soporte | [`backend-dotnet/docs/10-operacion.md`](../backend-dotnet/docs/10-operacion.md) | — |
| Pruebas | [`backend-dotnet/docs/08-testing.md`](../backend-dotnet/docs/08-testing.md) | Matrices de QA de [HU-021](../backend-dotnet/docs/qa/MATRIZ_PRUEBAS_HU021.md) y [HU-023](../backend-dotnet/docs/qa/MATRIZ_PRUEBAS_HU023.md), [regresión manual](../backend-dotnet/docs/qa/regresion-manual.md) |
| Backlog | Milestones e issues de GitHub (fuente de verdad) | Histórico (2026-09-29 a 2026-10-27): [`backlog/backlog_cotifly.md`](backlog/backlog_cotifly.md) |

## Carpetas de `docs/`

| Carpeta | Qué tiene |
|---|---|
| [`backlog/`](backlog/) | Backlog histórico (`backlog_cotifly.md`), pendientes vigentes por persona (`AAAA-MM-DD_PENDIENTES_*.md`) y borradores de HU activos |
| [`conexion_nexo/`](conexion_nexo/) | Mapeo de campos, preguntas y peticiones a Nexo y al cubicador, formato del Excel de licitación y próximos alcances |
| [`database/`](database/) | Documentación de la base de datos. Los scripts, seeders y el ejecutador están en [`db/`](../db/) |
| [`docs_front/`](docs_front/) | Lo que consume el frontend: contrato de la API web, guía del frontend, referencia consolidada de contratos y manual de ClientePro |
| [`infra/`](infra/) | Diagnósticos de infraestructura |
| [`archivo/`](archivo/) | Documentos históricos que ya no se mantienen (ver abajo) |

## Archivo

Estos documentos **no describen el sistema actual**. Se conservan como registro y cada uno indica qué lo reemplaza.

| Documento | Qué fue | Lo vigente |
|---|---|---|
| [`archivo/ESQUEMA_OBJETIVO_SQLSERVER_PARTE_B.md`](archivo/ESQUEMA_OBJETIVO_SQLSERVER_PARTE_B.md) | Diseño original del esquema SQL Server (HU-002.3) | [`database/ESQUEMA_OBJETIVO_SQLSERVER.md`](database/ESQUEMA_OBJETIVO_SQLSERVER.md) |
| [`archivo/PROPUESTA_REFACTOR_BD_COTIFLY.md`](archivo/PROPUESTA_REFACTOR_BD_COTIFLY.md) | Propuesta de refactor de la base de HU-002.1 (era Postgres) | [`database/ESQUEMA_OBJETIVO_SQLSERVER.md`](database/ESQUEMA_OBJETIVO_SQLSERVER.md) |
| [`archivo/HU-005.1-mapeo-campos-nexo.md`](archivo/HU-005.1-mapeo-campos-nexo.md) | Duplicado del mapeo de campos de Nexo | [`conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md`](conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md) |
| [`archivo/paridad-python-dotnet.md`](archivo/paridad-python-dotnet.md) | Plantilla de paridad Python vs .NET, cerrada sin ejecutarse, y la tabla de equivalencias de pruebas | [`backend-dotnet/docs/08-testing.md`](../backend-dotnet/docs/08-testing.md) |
| [`archivo/COMPONENTES_RETIRADOS.md`](archivo/COMPONENTES_RETIRADOS.md) | Google Chat, agente cotizador por chat, stack Python e integraciones que no se portaron (Drive, Sheets, SAP TM por exportación JSON u OData en vivo) | [`DEPLOY.md`](../DEPLOY.md) §7 (limpieza de infraestructura pendiente) |
| [`archivo/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md`](archivo/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md) | Borrador de HU-001 a HU-013 | Milestones de GitHub |
| [`archivo/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md`](archivo/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md) | Borrador de HU-021 (cerrada) | Milestone HU-021 y [ADR-019](../backend-dotnet/specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md) |
| [`archivo/backlog/2026-10-08_PENDIENTES_AL_FINALIZAR_CORRECCION_BD.md`](archivo/backlog/2026-10-08_PENDIENTES_AL_FINALIZAR_CORRECCION_BD.md) | Pendientes al cerrar HU-021 | [`BASE_DE_DATOS.md`](../BASE_DE_DATOS.md) §7 |

El código y la documentación del bot anterior en Python se retiraron en HU-022.1 (#146); su último estado está en el tag
de git `python-final`.

## Convenciones

- Cada documento empieza con su título y una línea `> **Estado:** ... · **Dueño:** ... · **Actualizado:** AAAA-MM-DD`.
  Las ADR usan su propia plantilla ([`backend-dotnet/specs/decisiones/README.md`](../backend-dotnet/specs/decisiones/README.md)).
- Un documento nuevo se agrega a la tabla "Por tema". Un borrador de HU va en `backlog/` con la fecha de creación como prefijo.
- Un documento que deja de describir el sistema se mueve a `archivo/` con una nota de "no vigente" y se redirigen sus enlaces.
- Los enlaces relativos se verifican con `python scripts/verificar_enlaces_md.py` (workflow `docs-enlaces`).
