# Regresión manual del backend .NET (HU-003.8, #14)

Ronda de regresión de los 3 flujos principales (nueva cotización, mis cotizaciones y envío de correo) contra el backend .NET **desplegado en el entorno de pruebas**, antes de considerar la migración lista para producción. La ejecuta Kevin (QA); el resultado se registra en este documento y en el issue #14.

- Estado: **pendiente de ejecución**.
- Responsable: Kevin (QA). Soporte: Duban (backend).
- Contrato de referencia: [`specs/openapi.yaml`](../../specs/openapi.yaml) y [`docs/CONTRATO_API_WEB.md`](../../../docs/CONTRATO_API_WEB.md).

## 1. Preparación

| Requisito | Cómo verificarlo | Listo |
|---|---|---|
| Backend .NET desplegado en pruebas, con la versión (commit) anotada abajo | `GET /health` responde 200 con `entorno` del ambiente de pruebas; `GET /health/ready` responde 200 | [ ] |
| Scripts `0002` y `0003` aplicados y `Roles.idRolExterno` con los IDROLES reales de ClientePro | `/health/ready` 200 y el login del paso RM-01 funciona | [ ] |
| Dos usuarios comerciales de prueba (A y B) y un gerente comercial en ClientePro | Existen en `dbsolucionesti` con su rol | [ ] |
| Proveedor de IA configurado (`IA__Proveedor`, `IA__Modelo`, `IA__ApiKey`) | Un "Hola" en `/web/chat` no responde `error: true` | [ ] |
| Correo habilitado contra el relay, con la IP del nodo autorizada, y un buzón de prueba accesible | `Correo__Habilitado=true`; vista previa responde 200 | [ ] |
| Herramienta: frontend Angular de ClientePro (HU-004) si ya está disponible, o Postman con la colección del contrato | — | [ ] |

Si no hay entorno de pruebas desplegado, la ronda puede adelantarse en local con `scripts/levantar-local.ps1` (usuarios `KCANO`, `APEREZ`, `LGOMEZ`; [06-ejecucion-local](../06-ejecucion-local.md#62-entorno-local-completo-con-sql-server)), agregando las variables `IA__*` y `Correo__*` al `.env.local`. Esa corrida no reemplaza la del entorno de pruebas.

Datos de la ejecución:

| Campo | Valor |
|---|---|
| Fecha | |
| Entorno y URL | |
| Versión del backend (commit o tag de la imagen) | |
| Proveedor y modelo de IA | |
| Frontend usado (Angular o Postman) | |

Convención: en Postman, guardar `access` del login en una variable y enviarlo como `Authorization: Bearer {{access}}` en cada llamada a `/web/*`. `sesion_id` de 8 a 64 caracteres `[A-Za-z0-9_-]`, uno nuevo por conversación.

## 2. Flujo 1: nueva cotización

| Id | Pasos | Resultado esperado | Obtenido | Estado |
|---|---|---|---|---|
| RM-01 | Login del comercial A: `POST /api/auth/login-clientepro {"idUsuario": "<usuario A>"}` (o entrar al módulo desde ClientePro) | 200 con `access`, `refresh` y `user.rol = comercial`. Un usuario sin rol de Cotifly recibe 401 `Credenciales inválidas` | | |
| RM-02 | Primer ingreso: `GET /web/usuario/telefono`; luego `PUT /web/usuario/telefono {"telefono": "+57 300 1234567"}` | GET devuelve `telefono: null` la primera vez; PUT 200 con el teléfono; un teléfono con letras responde 400 | | |
| RM-03 | `POST /web/chat` con una solicitud completa: línea Nacional, origen, destino, cliente y costo (por ejemplo "Cotiza un flete nacional de Cali a Bogotá para ACME, costo 2.850.000") | 200 `RespuestaChat`; `herramientas` incluye `obtener_margen` y `calcular_precio`; `margen` con `origen = politica_linea` (sin búsqueda de clientes) y `precio` con el precio calculado; el texto muestra la previsualización y pide confirmación | | |
| RM-04 | En la misma sesión, escribir "sí" **antes** de que haya una previsualización (en una sesión nueva) | No se genera PDF (`pdf: null`); el agente pide los datos que faltan | | |
| RM-05 | En la sesión de RM-03, pedir "Déjalo en 20 %" | `herramientas` incluye `registrar_ajuste_margen`; nuevo precio con el 20 % y nueva previsualización | | |
| RM-06 | Responder "Confirmo" | `pdf` con `folio`, `pdf_url` y `registrada_en_bd: true`; el folio tiene el formato `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]` de ADR-011, con las iniciales del comercial A (p. ej. `PCN-DG-25092026-001` para "Duban Guerra") y el consecutivo del día; si la cotización tiene NIT, este va después de `PCN-` | | |
| RM-07 | Descargar el PDF con `GET {pdf_url}` y el Bearer del comercial A | 200 `application/pdf`; 3 páginas; folio, cliente, línea, ruta y precio de la previsualización; firma con nombre, correo y "Cel" del teléfono de RM-02 | | |
| RM-08 | Repetir RM-03 a RM-06 con la línea "Distribución" (con tilde) | PDF generado; la línea se resuelve sin error (antes fallaba en el Python, issue #44) | | |
| RM-09 | Repetir con la línea Internacional | El PDF incluye el bloque aduanero; en Nacional no aparece | | |
| RM-10 | Enviar dos mensajes seguidos en la misma sesión sin esperar la respuesta | El segundo responde 200 `error: true` con "Todavía estoy respondiendo tu mensaje anterior..." | | |

## 3. Flujo 2: mis cotizaciones

| Id | Pasos | Resultado esperado | Obtenido | Estado |
|---|---|---|---|---|
| RM-20 | `GET /web/mis-cotizaciones` con el comercial A | 200 `{total, cotizaciones}` con las cotizaciones del Flujo 1, de la más reciente a la más antigua; `estado_envio = sin_enviar`; `pdf_url` presente | | |
| RM-21 | Descargar el PDF desde el listado | 200 `application/pdf` | | |
| RM-22 | `POST /web/cotizaciones/{folio}/seguimiento {"ganada": true}` | 200 `CotizacionResumen` con `ganada: true` | | |
| RM-23 | Otra cotización: `{"ganada": false, "motivo_rechazo": "Precio alto"}`; repetir con un motivo de más de 2000 caracteres | 200 con el motivo; con el motivo largo, 400 | | |
| RM-24 | Login del comercial B y `GET /web/mis-cotizaciones` | B no ve ninguna cotización de A | | |
| RM-25 | Con el Bearer de B: descargar un PDF de A, marcar seguimiento de un folio de A y pedir `GET /web/cotizaciones/{id de A}/nueva-version` | 404 `Cotización no encontrada o no es tuya.` en los tres casos, igual que un folio inexistente; la cotización de A no cambia | | |
| RM-26 | Con A: `GET /web/cotizaciones/{id}/nueva-version` y luego `POST /web/chat` en una sesión nueva con `cotizacion_origen_id = {id}`, pedir un cambio de precio y confirmar | 200 con `folio_base` y `version_siguiente = 2`; el PDF nuevo sale con el folio base y `-v2`; el listado muestra la versión 2 | | |
| RM-27 | Sin token: `GET /web/mis-cotizaciones` | 401 `Inicia sesión de nuevo.` | | |

## 4. Flujo 3: envío de correo

| Id | Pasos | Resultado esperado | Obtenido | Estado |
|---|---|---|---|---|
| RM-40 | `POST /web/cotizaciones/{folio}/correo/vista-previa` con cuerpo vacío | 200 `VistaPreviaCorreo`: asunto `Cotización {folio} — Botero Soto`, cuerpo con la plantilla, adjunto `{folio}.pdf` | | |
| RM-41 | `POST /web/cotizaciones/{folio}/enviar {"para": ["<buzón de prueba>"]}` | 200 `RespuestaEnvio` (`enviado: true`, `message_id`). En el buzón: remitente y `Reply-To` = correo del comercial, asunto de RM-40, cuerpo en texto y HTML, PDF adjunto que abre. `mis-cotizaciones` muestra `estado_envio = enviada` | | |
| RM-42 | Repetir RM-41 de inmediato con los mismos destinatarios (en otro orden de mayúsculas) | 400 `ErrorNegocio` `CORREO_DUPLICADO`; no llega un segundo correo | | |
| RM-43 | Enviar la misma cotización a otro destinatario | 200; llega el correo; `estado_envio = reenviada` | | |
| RM-44 | Enviar con `para` vacío o con un correo mal formado | 400 `ErrorWeb` (`CORREO_INVALIDO`) | | |
| RM-45 | `GET /web/clientes/{nit}/destinatarios-previos` para una cotización con NIT (si la búsqueda de clientes aún no existe, se registra como no aplicable) | 200 con los destinatarios del último envío del comercial a ese NIT | | |
| RM-46 | Por el chat: tras generar un PDF, escribir "Envíala a <buzón de prueba>" | `herramientas` incluye `enviar_correo`; llega el correo | | |
| RM-47 | Con el Bearer de B, enviar un folio de A | 404; no se envía nada | | |

## 5. Hallazgos

| Id del caso | Descripción | Severidad (bloqueante, alta, media, baja) | Issue | Estado |
|---|---|---|---|---|
| | | | | |

## 6. Resultado

| Flujo | Casos | Aprobados | Fallidos | No aplica |
|---|---|---|---|---|
| Nueva cotización | 10 | | | |
| Mis cotizaciones | 8 | | | |
| Envío de correo | 8 | | | |

Aprobación de QA (nombre, fecha y decisión): pendiente.
