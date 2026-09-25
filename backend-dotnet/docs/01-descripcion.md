# 1. Descripción

Cotifly es el bot cotizador de Botero Soto: un comercial conversa con un agente de IA (consola web de ClientePro o Google Chat), el agente reúne los datos, resuelve margen y precio, genera el PDF de la cotización y la envía por correo. Este proyecto reemplaza por completo a los servicios Python `orquestador` y `mcp_server` (corte big-bang, [ADR-001](../specs/decisiones/ADR-001-estrategia-de-corte.md)). El código Python queda intacto hasta validar el backend .NET en producción.

## 1.1 Objetivo

Migrar el backend de Cotifly a C# / .NET 10 (milestone HU-003) con Clean Architecture, conservando el comportamiento de negocio del Python (política de márgenes, cálculo de precio, formato del PDF, flujo de correo) salvo los cambios que el equipo decidió de forma explícita, y exponer el contrato congelado que consume el módulo Angular de ClientePro (HU-004, [`docs/CONTRATO_API_WEB.md`](../../docs/CONTRATO_API_WEB.md)).

## 1.2 Usuarios y roles

| Rol interno | Quién | Qué puede hacer |
|---|---|---|
| `comercial` | Comercial de Botero Soto con rol "Comercial" en ClientePro | Cotizar por la consola web o por Google Chat, ver y hacer seguimiento a sus cotizaciones, enviarlas por correo, registrar su teléfono |
| `gerente_comercial` | Gerente comercial | Todo lo del comercial, más la administración de las líneas de negocio (margen, método, estado; HU-004.9) y las métricas de su regional (HU-010.1, borrador) |

El rol se obtiene del almacén corporativo de ClientePro (`dbsolucionesti`) en cada login y se compara con la asignación interna (verificación doble, [ADR-009](../specs/decisiones/ADR-009-autenticacion-sso-clientepro.md)). Cotifly no tiene login propio.

## 1.3 Capacidades

- **Login SSO desde ClientePro** con verificación doble, access JWT de 60 minutos, refresh rotado de 30 días y logout que revoca ambos.
- **Agente de IA agnóstico al proveedor** (`POST /web/chat` y webhook de Google Chat) con estado de conversación estructurado: el LLM redacta y elige herramientas; los datos y los números salen de las herramientas y del estado ([ADR-003](../specs/decisiones/ADR-003-orquestacion-ia-agnostica.md), [ADR-004](../specs/decisiones/ADR-004-estado-conversacion-slot-filling.md)).
- **Validador de la respuesta final** del agente con JSON Schema, un reintento y respaldo controlado (HU-003.10).
- **Margen** desde SAP TM (export JSON u OData en vivo) con respaldo en la política de la línea, y margen negociado auditado.
- **Precio** por margen o markup con redondeo half-to-even sobre `decimal`.
- **PDF** de la cotización con PDFsharp-MigraDoc (una tarifa o varias secciones), folio generado por el servidor ([ADR-011](../specs/decisiones/ADR-011-folio-e-identidad-google-chat.md)) y almacén en volumen.
- **Correo** por el relay interno con vista previa, destinatarios previos, deduplicación de 5 minutos y estado `enviada`/`reenviada`.
- **Consola web** `/web/*`: mis cotizaciones, seguimiento ganada/perdida, nueva versión, teléfono del comercial, descarga autenticada del PDF, administración de líneas de negocio y métricas.
- **Auditoría** de cada herramienta y de los accesos sensibles en `EventosHerramienta`.

## 1.4 Estado por issue (milestone HU-003)

| Issue | HU | Entrega |
|---|---|---|
| #7 | HU-003.1 | Decisiones de arquitectura (ADR-001 a ADR-012) |
| #8 | HU-003.2 | Solución, capas, convenciones, configuración validada al arrancar, `GET /health` y `GET /health/ready` |
| #9 | HU-003.3 | Modelo de dominio y persistencia EF Core Database First sobre SQL Server 2019 |
| #10 | HU-003.4 | Módulos Precio, Margen (incluido el margen negociado) y Tarifas |
| #13 | HU-003.7 | Fuentes reales de SAP TM (export JSON y OData en vivo), elegidas por `SapTm:Modo` |
| #11 | HU-003.5 | PDF, correo y auditoría; teléfono del comercial y seguimiento como casos de uso |
| #27 | HU-006.3 | `Dockerfile` multi-stage con runtime chiseled, sonda propia y usuario no root |
| #12 parte A | HU-003.6 | Login SSO ClientePro con verificación doble, refresh y logout (`/api/auth/*`) |
| #12 parte B | HU-003.6 | API web `/web/*`, folio del servidor, descarga autenticada del PDF, administración de líneas (HU-004.9), métricas en borrador (HU-010.1), CORS, encabezados reenviados y endurecimiento HTTP |
| #12 parte C | HU-003.6 | Agente de IA, `POST /web/chat`, webhook de Google Chat con worker y enlace firmado del PDF |
| #16 | HU-003.10 | Validador de la respuesta final del agente |
| #14 | HU-003.8 | Integración continua, scripts de ejecución local, cobertura, documentación técnica y casos de regresión manual (este conjunto de documentos) |
| #15 | HU-003.9 | Paridad funcional Python vs .NET ejecutada por QA: pendiente (plantilla en [qa/paridad-python-dotnet.md](qa/paridad-python-dotnet.md)) |

## 1.5 Fuera del alcance actual

| Tema | Dónde se resuelve |
|---|---|
| Costeo real de rutas (Nexo) | HU-005; mientras tanto `buscar_tarifa` responde `TARIFA_FUENTE_NO_DISPONIBLE` y el comercial escribe el costo |
| Búsqueda real de clientes (Grovity/Nexo) | HU-005.2, [ADR-005](../specs/decisiones/ADR-005-busqueda-clientes-desde-chat.md) |
| Regional del comercial desde `DWCubosEBSSAP` | Pendiente; hasta entonces el gerente solo ve su propia fila de métricas |
| Conversión a USD con TRM | HU-012 (`trmAplicada` queda en 0) |
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
| Folio | Identificador de la cotización que genera el servidor ([ADR-011](../specs/decisiones/ADR-011-folio-e-identidad-google-chat.md)); una nueva versión agrega `-vN` |
| Slot-filling | Estado estructurado de la conversación: lo resuelto por las herramientas (cliente, línea, margen, precio, folio) viaja entre turnos sin depender del texto |
| Previsualización | Resumen con el precio que el agente muestra antes de generar el PDF; el PDF exige la confirmación explícita del comercial |
| Verificación doble | El rol externo de ClientePro debe coincidir con la asignación interna en `UsuariosRoles` en cada login |
| JIT | Alta automática del usuario en `UsuariosWeb` en su primer ingreso |
| MTP | Microsoft.Testing.Platform, el ejecutor de pruebas de xUnit v3 en el SDK 10 |
