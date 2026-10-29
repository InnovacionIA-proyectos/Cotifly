# 1. Descripción

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

Cotifly es el bot cotizador de Botero Soto: un comercial captura la solicitud en la consola web de ClientePro, Nexo la costea, el backend resuelve margen y precio, genera el PDF de la cotización y la envía por correo; la IA solo redacta textos y asiste sobre una cotización ya hecha ([ADR-018](../specs/decisiones/ADR-018-pipeline-determinista-ia-etapa-final.md)). Hasta HU-013.9 (#105) la cotización la armaba un agente conversacional, también desde Google Chat; ambos se retiraron. Este proyecto reemplazó por completo al bot anterior en Python (corte big-bang, [ADR-001](../specs/decisiones/ADR-001-estrategia-de-corte.md)), que se retiró del repositorio en HU-022.1 (#146; su último estado está en el tag `python-final`).

## 1.1 Objetivo

Backend de Cotifly en C# / .NET 10 (milestone HU-003) con Clean Architecture. La migración conservó el comportamiento de negocio del bot anterior (política de márgenes, cálculo de precio, formato del PDF, flujo de correo) salvo los cambios que el equipo decidió de forma explícita. El backend expone el contrato congelado que consume el módulo Angular de ClientePro (HU-004, [`docs/docs_front/CONTRATO_API_WEB.md`](../../docs/docs_front/CONTRATO_API_WEB.md)).

## 1.2 Usuarios y roles

| Rol interno | Quién | Qué puede hacer |
|---|---|---|
| `comercial` | Comercial de Botero Soto con rol "Comercial" en ClientePro | Cotizar por la consola web, ver y hacer seguimiento a sus cotizaciones, enviarlas por correo, registrar su teléfono |
| `gerente_comercial` | Gerente comercial | Todo lo del comercial, más la administración de las líneas de negocio (margen, método, estado; HU-004.9) y el tablero de métricas de su regional, filtrable por comercial (HU-010.1, [ADR-016](../specs/decisiones/ADR-016-metricas-agregadas-y-regional.md)) |

El rol se obtiene del almacén corporativo de ClientePro (`dbsolucionesti`) en cada login y se compara con la asignación interna (verificación doble, [ADR-009](../specs/decisiones/ADR-009-autenticacion-sso-clientepro.md)). Cotifly no tiene login propio.

## 1.3 Capacidades

- **Login SSO desde ClientePro** con verificación doble, access JWT de 60 minutos, refresh rotado de 30 días y logout que revoca ambos.
- **Asistente IA por folio, agnóstico al proveedor** (`POST /web/chat` con `folio`): consulta la cotización y propone ajustes de margen, re-solicitudes del costeo y servicios conexos que el comercial confirma; ningún monto llega al modelo ([ADR-003](../specs/decisiones/ADR-003-orquestacion-ia-agnostica.md), [ADR-018](../specs/decisiones/ADR-018-pipeline-determinista-ia-etapa-final.md)). El agente cotizador con slot filling ([ADR-004](../specs/decisiones/ADR-004-estado-conversacion-slot-filling.md)) y el webhook de Google Chat se retiraron en HU-013.9 (#105).
- **Validador de la respuesta final** del agente con JSON Schema, un reintento y respaldo controlado (HU-003.10).
- **Margen** desde SAP TM (la copia local que sincroniza Datos a diario, HU-042) con respaldo en la política de la línea, y margen negociado auditado.
- **Precio** por margen o markup con redondeo half-to-even sobre `decimal`.
- **PDF** de la cotización con PDFsharp-MigraDoc (una tarifa o varias secciones), folio generado por el servidor ([ADR-011](../specs/decisiones/ADR-011-folio-e-identidad-google-chat.md)) y almacén en volumen.
- **Correo** por el relay interno con vista previa, destinatarios previos, deduplicación de 5 minutos y estado `enviada`/`reenviada`.
- **Consola web** `/web/*`: mis cotizaciones, seguimiento ganada/perdida, nueva versión, teléfono del comercial, descarga autenticada del PDF, administración de líneas de negocio y métricas.
- **Integración con Nexo** (con `Nexo__UrlBase` configurada): búsqueda de clientes y prospectos para el formulario, catálogo de municipios y catálogos de flete (tipo de vehículo, carrocería y clase de viaje). La cubicación ya no pasa por Nexo: "Calcular cubicaje" (`POST /web/cubicacion`) y los camiones de cada ruta los recomienda el **cubicador** (`Cubicador__UrlBase`, HU-023, ADR-020).
- **Costeo en Nexo** por formulario o por Excel de licitación: una solicitud con rutas y camiones, polling del costeo, servicios conexos por ruta y resultado ganada/perdida ([ADR-015](../specs/decisiones/ADR-015-costeo-nexo.md), [ADR-017](../specs/decisiones/ADR-017-licitaciones-multilinea.md), [ADR-019](../specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md)).
- **Cotización en USD** con la TRM del día fijada al crear (HU-012, [ADR-014](../specs/decisiones/ADR-014-trm-y-cotizaciones-en-usd.md)).
- **Auditoría** de cada herramienta y de los accesos sensibles en `EventosHerramienta`.

## 1.4 Estado por issue (milestone HU-003)

| Issue | HU | Entrega |
|---|---|---|
| #7 | HU-003.1 | Decisiones de arquitectura (ADR-001 a ADR-012) |
| #8 | HU-003.2 | Solución, capas, convenciones, configuración validada al arrancar, `GET /health` y `GET /health/ready` |
| #9 | HU-003.3 | Modelo de dominio y persistencia EF Core Database First sobre SQL Server 2019 |
| #10 | HU-003.4 | Módulos Precio, Margen (incluido el margen negociado) y Tarifas |
| #13 | HU-003.7 | Fuentes reales de SAP TM (export JSON y OData en vivo), elegidas por `SapTm:Modo`; reemplazadas por la copia local en HU-042.2 (#243, ADR-027) |
| #11 | HU-003.5 | PDF, correo y auditoría; teléfono del comercial y seguimiento como casos de uso |
| #27 | HU-006.3 | `Dockerfile` multi-stage con runtime chiseled, sonda propia y usuario no root |
| #12 parte A | HU-003.6 | Login SSO ClientePro con verificación doble, refresh y logout (`/api/auth/*`) |
| #12 parte B | HU-003.6 | API web `/web/*`, folio del servidor, descarga autenticada del PDF, administración de líneas (HU-004.9), métricas en borrador (HU-010.1), CORS, encabezados reenviados y endurecimiento HTTP |
| #12 parte C | HU-003.6 | Agente de IA, `POST /web/chat`, webhook de Google Chat con worker y enlace firmado del PDF (el agente cotizador y Google Chat se retiraron en HU-013.9, #105) |
| #16 | HU-003.10 | Validador de la respuesta final del agente |
| #14 | HU-003.8 | Integración continua, scripts de ejecución local, cobertura, documentación técnica y casos de regresión manual (este conjunto de documentos) |
| #20 (PR #65) | HU-005.2, HU-005.3, HU-005.4, HU-005.13 | Fuera del milestone HU-003: integración con Nexo (`GET /web/clientes?q=` y `buscar_cliente`, `GET /web/municipios`, `GET /web/catalogos-flete`, `POST /web/cubicacion`) |
| #15 | HU-003.9 | Paridad funcional Python vs .NET: **cerrada por decisión del usuario (2026-10-13, HU-022.1): el Python se retiró del repositorio (tag `python-final`), así que ya no hay contra qué comparar** (plantilla histórica en [qa/paridad-python-dotnet.md](../../docs/archivo/paridad-python-dotnet.md)) |

## 1.5 Fuera del alcance actual

| Tema | Dónde se resuelve |
|---|---|
| Criterio de coincidencia exacta de la búsqueda de clientes | Pendiente con el equipo de Nexo ([ADR-005](../specs/decisiones/ADR-005-busqueda-clientes-desde-chat.md)); hasta entonces es exacta solo con un único candidato |
| Grovity (ClientePro) desde el agente | Propuesta sin aprobar en ADR-005 y sin objeto desde HU-013.9 (el agente que la usaría se retiró); el alta de prospectos del formulario no la necesita (la resuelve `clientepro-frontend`, corrección del 2026-09-27) |
| Regional del comercial desde `DWCubosEBSSAP` | Adaptador listo tras `ConnectionStrings__Regional` (ADR-016); falta que el DBA confirme la tabla y dé acceso. Mientras tanto la fija el DBA en `UsuariosWeb.regional`; sin ella el tablero del gerente responde 409 `REGIONAL_NO_ASIGNADA` |
| Score de urgencia y deadline vencido (Nexo) en el tablero | Pendiente del contrato de Nexo (ADR-016 §8) |
| Sincronización a Google Sheets y PDF en S3/MinIO | Diferidas, [ADR-012](../specs/decisiones/ADR-012-integraciones-diferidas-sheets-y-s3.md) |
| Despliegue en Docker Swarm con Dokploy | HU-006 |
| Frontend Angular | HU-004 (`clientepro-frontend`) |

## 1.6 Glosario

| Término | Significado |
|---|---|
| BP | Business Partner de SAP: identificador del cliente con el que se consulta SAP TM (nunca el NIT) |
| TrqType | Tipo de solicitud de transporte de SAP TM; cada línea de negocio se mapea a uno (`SapMapeoTrqType`) |
| Política de la línea | Margen y método por línea de negocio en `MargenesPolitica`, editables por el gerente comercial |
| Margen / markup | `margen`: precio = costo / (1 − m). `markup`: precio = costo × (1 + m) |
| Folio | Identificador de la cotización que genera el servidor: `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`, con las iniciales del comercial y un consecutivo diario por iniciales (ej. `PCN-DG-25092026-001`, [ADR-011](../specs/decisiones/ADR-011-folio-e-identidad-google-chat.md)); una nueva versión agrega `-vN` |
| Slot-filling | Estado estructurado de la conversación del agente cotizador legado (cliente, línea, margen, precio, folio); retirado en HU-013.9. Hoy el estado de la sesión solo guarda la cotización del folio |
| Acción propuesta | Cambio que propone el asistente por folio (margen, re-solicitud, conexos), armado por el servidor; el frontend lo ejecuta cuando el comercial pulsa Confirmar |
| Verificación doble | El rol externo de ClientePro debe coincidir con la asignación interna en `UsuariosRoles` en cada login |
| JIT | Alta automática del usuario en `UsuariosWeb` en su primer ingreso |
| MTP | Microsoft.Testing.Platform, el ejecutor de pruebas de xUnit v3 en el SDK 10 |
