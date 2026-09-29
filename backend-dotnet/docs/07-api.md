# 7. API

La fuente de verdad del contrato es [`specs/openapi.yaml`](../specs/openapi.yaml) (OpenAPI 3.0.3, #17, SDD según [ADR-010](../specs/decisiones/ADR-010-metodologia-sdd-tdd-bdd.md)). El resumen para el frontend está en [`docs/CONTRATO_API_WEB.md`](../../docs/CONTRATO_API_WEB.md). Este documento no repite los esquemas: resume lo implementado, la autenticación, los errores y los límites. Un cambio de contrato empieza siempre en `openapi.yaml`.

Contenido:

- [7.1 Convenciones](#71-convenciones)
- [7.2 Autenticación y autorización](#72-autenticación-y-autorización)
- [7.3 Endpoints](#73-endpoints)
- [7.4 Errores](#74-errores)
- [7.5 Ejemplos](#75-ejemplos)
- [7.6 Límites, CORS y encabezados](#76-límites-cors-y-encabezados)
- [7.7 Verificación del contrato](#77-verificación-del-contrato)

## 7.1 Convenciones

| Grupo | Serialización | Notas |
|---|---|---|
| `/web/*` | snake_case (`clsJsonSnakeCase`, `clsFormateadorEntradaWeb`/`clsFormateadorSalidaWeb`) | Nulos siempre escritos; números estrictos (`"0.2"` como texto es un cuerpo inválido) |
| `/api/auth/*` | camelCase del patrón "Login SSO — Cliente Pro" (`idUsuario`, `debeCambiarPassword`) | Excepción definida por el contrato: `user.nombre_completo` |
| `/health`, `/health/ready`, `/pdfs/*`, `/chat/events` | JSON por defecto de ASP.NET Core o binario | `/pdfs/*` devuelve `application/pdf` inline |

- Operaciones con `x-estado: borrador` (métricas de HU-010.1) no están congeladas.
- Las rutas de los controllers son explícitas (`[Route("web/...")]`), nunca `[controller]`.
- `ubicacion` del usuario sale `null` hasta que se cruce la regional con `DimEjecutivo`.

## 7.2 Autenticación y autorización

- Login: `POST /api/auth/login-clientepro` (anónimo). Flujo y verificación doble en [2.9](02-arquitectura.md#29-identidad-login-sso-con-verificación-doble) y [4.2](04-flujo-del-sistema.md#42-inicio-de-sesión-y-renovación).
- Cada llamada a `/web/*` y `/pdfs/*` lleva `Authorization: Bearer <access>` (JWT HS256, 60 minutos; claims `sub`, `jti`, `username`, `rol`, `iss`, `aud`, `iat`, `exp`).
- Sin token, vencido, firmado con otra clave o revocado: `401 {"error":"Inicia sesión de nuevo."}`. Rol insuficiente: `403 {"error":"No tienes permiso para esta operación."}`.
- Políticas: `Comercial` (roles `comercial` y `gerente_comercial`) en toda la consola; `GerenteComercial` además en `/web/admin/*`. Los casos de uso sensibles revalidan el rol.
- `/pdfs/{archivo}` acepta también el enlace firmado `?t=` de Google Chat (HMAC-SHA256, 15 minutos, solo el dueño).
- `/chat/events` no usa el Bearer de ClientePro: exige el JWT de Google (RS256, audiencia `GChat:ProjectNumber`).
- `/health` y `/health/ready` son anónimos.

## 7.3 Endpoints

El contrato congelado es `specs/openapi.yaml` (#17), fuente de verdad (SDD, ADR-010). Hoy está implementado:

| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/health` | `200 {"status":"ok","servicio":"cotifly-api","entorno":"<ASPNETCORE_ENVIRONMENT>"}` |
| GET | `/health/ready` | `200 {"status":"ready"}` si `dbo_cotifly` acepta conexiones y tiene el 0002 aplicado; `503 {"status":"unavailable","detalle":"..."}` si no (sin cadena configurada, base caída, `esquema incompleto: falta 0002` o `esquema incompleto: falta 0004`) |
| POST | `/api/auth/login-clientepro` | `200 LoginResponse` (`access`, `refresh`, `user{id, username, email, nombre_completo, rol, ubicacion}`, `debeCambiarPassword=false`); `400 ErrorWeb` (cuerpo inválido); `401 {"error":"Credenciales inválidas"}` (cualquier rechazo); `429 ErrorWeb` |
| POST | `/api/auth/refresh` | `200 LoginResponse` con el par rotado; `401 {"error":"Inicia sesión de nuevo."}` (incluye cuerpo inválido, reuso y rotación perdida); `429` |
| POST | `/api/auth/logout` | Siempre `204` (con o sin `Authorization: Bearer`, con o sin `{"refresh": ...}`); `429` |
| GET | `/web/mis-cotizaciones` | `200 MisCotizaciones` `{total, cotizaciones: [CotizacionResumen]}`: solo las del comercial, máx. 100, de la más reciente a la más antigua |
| POST | `/web/cotizaciones/{folio}/enviar` | `200 RespuestaEnvio`; `400 ErrorWeb` (`CORREO_INVALIDO`) o `ErrorNegocio` (duplicado, sin PDF, relay); `404` si no es suya; `503 ErrorNegocio` `CORREO_DESHABILITADO` |
| POST | `/web/cotizaciones/{folio}/correo/vista-previa` | `200 VistaPreviaCorreo` (cuerpo opcional); `404` |
| POST | `/web/cotizaciones/{folio}/seguimiento` | `200 CotizacionResumen`; `400` (motivo > 2000, cuerpo inválido); `404` |
| GET | `/web/cotizaciones/{id}/nueva-version` | `200 DatosNuevaVersion` (`folio_base`, `version_siguiente`...); `404` (también si `id` no es UUID) |
| GET | `/web/clientes?q=` | `200 ResultadoBusquedaClientes` `{exacta, candidatos: [{nit_cliente, nombre, origen_cliente, bp}]}` desde Nexo (HU-005.2; `candidatos: []` si no hubo coincidencias; `exacta=true` solo con un único candidato); `400 ErrorWeb` si falta `q`; `400 ErrorNegocio` `CLIENTE_BUSQUEDA_NO_DISPONIBLE` si Nexo no está configurado, no responde o el comercial no tiene `idUsuarioExterno` |
| GET | `/web/clientes/{nit}/destinatarios-previos` | `200 Destinatarios` del último envío del comercial a ese NIT (vacío si no hay); `400` si el NIT supera 20 caracteres |
| GET | `/web/municipios` | `200 [Municipio]` `{id, codigo_dane, municipio, departamento, pais}`, catálogo completo de Nexo sin paginar (HU-005.13); `400 ErrorNegocio` `MUNICIPIOS_NO_DISPONIBLE` |
| GET | `/web/catalogos-flete` | `200 CatalogosFlete` `{tipos_vehiculo, tipos_carroceria, clases_viaje}` (cada opción `{valor, etiqueta}`) desde la biblioteca SAP TM histórico de Nexo (HU-005.3); `400 ErrorNegocio` `CATALOGOS_FLETE_NO_DISPONIBLE` |
| POST | `/web/cubicacion` | Cuerpo `CubicacionRequest` `{items: [{descripcion?, cantidad, largo_cm, ancho_cm, alto_cm, peso_kg?}], clase_viaje?}` (centímetros y kilogramos). `200 ResultadoCubicacion` `{sugerencia, sugerencia_flota, advertencias, extradimension}`, sin persistir nada (HU-005.4); `400 ErrorNegocio` `ITEMS_REQUERIDOS` (falta `items` o viene vacío), `CUBICACION_DEMASIADO_GRANDE` (Nexo/Cubicador respondió 400: típicamente el límite de bultos por solicitud, también medidas inválidas) o `CUBICACION_NO_DISPONIBLE` |
| GET / PUT | `/web/usuario/telefono` | `200 TelefonoComercial`; PUT `400` fuera del patrón; `401` si el comercial ya no existe |
| GET | `/web/admin/lineas-negocio` | `200 [LineaNegocio]` (todas); `403` salvo `gerente_comercial` |
| PUT | `/web/admin/lineas-negocio/{id}` | `200 LineaNegocio` (auditado); `400` (margen fuera de 0–0,99, método, campo faltante, número como texto); `403`; `404` |
| GET | `/web/metricas/actividad` | **Borrador.** `200 [ActividadComercial]`: el comercial su fila; el gerente su regional |
| GET | `/web/metricas/ajustes-margen` | **Borrador.** `200 [AjustesMargenComercial]`, mismo alcance |
| GET | `/pdfs/{archivo}` | `200 application/pdf` inline (dueño o gerente de su regional con Bearer; o el dueño con el enlace firmado `?t=` de Google Chat, 15 min); `401` (sin sesión, o enlace alterado o vencido); `404 ErrorWeb` (no existe o no es tuyo) |
| POST | `/web/chat` | `200 RespuestaChat` `{texto, herramientas, precio, margen, pdf}` (los números salen de las herramientas del turno); `200 RespuestaChatError` `{texto, error: true}` ante un fallo del agente o del proveedor (texto del límite de peticiones del Python si es un 429); `400` (`sesion_id inválido`, `moneda inválida`, mensaje vacío o de más de 8000); `moneda` opcional (`COP`\|`USD`, selector de HU-012.4): con `USD` el `precio` llega convertido con la TRM oficial del día (`moneda`, `trm_aplicada`, `precio_cop`, `trm_vigencia_desde`/`trm_vigencia_hasta`; costo y utilidad en COP); `errores` (opcional, aditivo, HU-012.4): `[{herramienta, codigo}]` con los errores de negocio de las herramientas del turno (ej. `{"herramienta":"calcular_precio","codigo":"TRM_NO_DISPONIBLE"}`), para que el frontend reaccione sin interpretar el texto; `401`; `404 {"error":"Sesión no encontrada."}` (sesión de otro comercial); `429` (`LimiteChat`) |
| POST | `/chat/events` | Webhook de Google Chat (sin Bearer de ClientePro): `200` acuse (`{"text": ...}`, `hostAppDataAction...` en add-on, `{}` si no es mensaje); `400 {"error":"payload no es JSON"}`; `401 {"error":"JWT inválido o ausente"}` |

Los cuatro endpoints de Nexo (`/web/clientes?q=`, `/web/municipios`, `/web/catalogos-flete`, `/web/cubicacion`) usan la política `Comercial` como el resto de la consola. El backend reenvía la consulta a `Nexo__UrlBase` sin token: identifica al comercial ante Nexo con `X-Portal-Usuario-Id` (su `UsuariosWeb.idUsuarioExterno`, resuelto a partir del `sub` del JWT, nunca del cliente). Sin `Nexo__UrlBase` los cuatro responden su código `*_NO_DISPONIBLE`. `GET /web/municipios` sirve la foto del catálogo que guarda `Nexo__MinutosCacheMunicipios` (60 minutos por defecto, HU-005.13), la misma que usa el agente para resolver el origen y el destino del chat; el contrato no cambia. Detalle en [2.14](02-arquitectura.md#214-integración-con-nexo) y [3.5](03-infraestructura.md#35-servicios-externos).

Endpoints protegidos (parte B de #12): `Authorization: Bearer <access>`; sin token, vencido, de otra clave o revocado → `401 {"error":"Inicia sesión de nuevo."}`; rol insuficiente → `403 {"error":"No tienes permiso para esta operación."}`.

`/health/ready` ejecuta los health checks con etiqueta `ready` (hoy `clsBaseDatosHealthCheck`: `CanConnectAsync` sobre `clsCotiflyDbContext` más verificación de `Sesiones.estadoAgente` y `TokensRefresco`, con un límite de 5 s para toda la sonda). `detalle` nunca incluye la cadena ni el servidor; la excepción queda en el log. Ninguno de los dos exige autenticación. El `HEALTHCHECK` de la imagen (#27) consulta `/health` (vida), no `/health/ready`: una base caída no debe hacer que Swarm reinicie el contenedor.

Todas las operaciones del contrato están implementadas (`clsContratoRutasTests.PendientesParteC` quedó vacía con la parte C de #12). Todos los endpoints de `/web/*` y `/pdfs` sin token responden `401 {"error":"Inicia sesión de nuevo."}` (prueba que recorre el contrato).

## 7.4 Errores

Dos formas de error, ambas del contrato:

```json
{ "error": "Cotización no encontrada o no es tuya." }
```

```json
{ "error": { "codigo": "CORREO_DUPLICADO", "mensaje": "...", "detalle": { "motivo": "..." } } }
```

La primera (`ErrorWeb`) es la de autenticación, validación y acceso; la segunda (`ErrorNegocio`) la de los códigos de negocio de los casos de uso y de las herramientas del agente. Los códigos válidos son el enum de `ErrorNegocio` en `openapi.yaml`; `Cotifly.Api.Tests` verifica que cada `clsCodigosError` exista allí.

Traducción de los códigos de los casos de uso a HTTP en `/web/*` (`Comun/clsRespuestasWeb`, una sola tabla para todos los controllers):

Errores de `/web/*` (`clsRespuestasWeb`, cumple la tabla "Para #12" de abajo):

| Código del caso de uso | HTTP |
|---|---|
| `COTIZACION_NO_ENCONTRADA`, `NO_ENCONTRADO` (no existe o no es tuya; PDF ajeno) | 404 `ErrorWeb` (`Cotización no encontrada o no es tuya.`, igual en ambos casos) |
| `NO_AUTENTICADO`, `USUARIO_NO_ENCONTRADO` | 401 `ErrorWeb` (`Inicia sesión de nuevo.`) |
| `SIN_PERMISO` (rol revalidado en el caso de uso) | 403 `ErrorWeb` |
| `CORREO_DESHABILITADO` | 503 `ErrorNegocio` |
| `SOLICITUD_INVALIDA`, `TELEFONO_INVALIDO`, `CORREO_INVALIDO`, `SEGUIMIENTO_INVALIDO`, cuerpo mal formado | 400 `ErrorWeb` |
| `TRM_NO_DISPONIBLE` (HU-012: la fuente oficial de la TRM no respondió; no se cotiza en USD) | Lo devuelven las herramientas `calcular_precio` y `registrar_ajuste_margen` del agente; en `/web/chat` es 200 `RespuestaChat` con `precio: null` y `errores: [{"herramienta":"calcular_precio","codigo":"TRM_NO_DISPONIBLE"}]` (el agente además explica que puede reintentar o cotizar en COP), no un HTTP de error |
| `TARIFA_CONFIG_INVALIDA` (la fuente de costeo devolvió una tarifa en una moneda distinta de COP) | Lo devuelve `buscar_tarifa`; llega en `errores` de `RespuestaChat` |
| Resto de códigos de negocio (`CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO`, `CORREO_ENVIO_FALLIDO`, `CONFIRMACION_REQUERIDA`, y los de Nexo: `CLIENTE_BUSQUEDA_NO_DISPONIBLE`, `MUNICIPIOS_NO_DISPONIBLE`, `CATALOGOS_FLETE_NO_DISPONIBLE`, `ITEMS_REQUERIDOS`, `CUBICACION_NO_DISPONIBLE`, `CUBICACION_DEMASIADO_GRANDE`...) | 400 `ErrorNegocio` (`detalle` como objeto `{"motivo": ...}`) |
| Cuerpo de más de `SeguridadHttp:LimiteCuerpoWebBytes` | 413 `ErrorWeb` (fuera del contrato: transporte) |

La validación automática de `[ApiController]` responde `400 ErrorWeb` (no `ProblemDetails`). `/api/auth/*`: 400 `ErrorWeb` por cuerpo inválido en el login, 401 genérico para cualquier rechazo, refresh con cuerpo inválido también 401, logout siempre 204, 429 al pasar el límite.

Tabla de origen, definida en #11 para los endpoints de #12 y cumplida por `clsRespuestasWeb` (la citan las pruebas de `clsRespuestasWebTests` y `clsWebEndpointsTests`):

**Para #12 (endpoints y agente):**

| Caso | Respuesta HTTP en `/web/*` |
|---|---|
| `COTIZACION_NO_ENCONTRADA` (no existe o no es suya) | 404 `ErrorWeb` (`NoEsTuya`) |
| `CORREO_DESHABILITADO` | 503 |
| `USUARIO_NO_ENCONTRADO` (teléfono: el comercial del token ya no existe) | 401 `ErrorWeb` |
| `TELEFONO_INVALIDO`, `CORREO_INVALIDO`, `SEGUIMIENTO_INVALIDO` | 400 `ErrorWeb` |
| Resto de códigos de envío (`CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO`, `CORREO_ENVIO_FALLIDO`, `CONFIRMACION_REQUERIDA`) | 400 `ErrorNegocio` |


## 7.5 Ejemplos

Login:

```http
POST /api/auth/login-clientepro
Content-Type: application/json

{ "idUsuario": "APEREZ" }
```

```json
{
  "access": "eyJhbGciOiJIUzI1NiIs...",
  "refresh": "fI1FJv7TFUPtgck3URCDw-VCY...",
  "user": {
    "id": "968c9f61-b1f1-44a8-bb23-7a3d31d7d602",
    "username": "APEREZ",
    "email": "aperez@example.com",
    "nombre_completo": "Ana Pérez (dev)",
    "rol": "comercial",
    "ubicacion": null
  },
  "debeCambiarPassword": false
}
```

Turno de chat:

```http
POST /web/chat
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
Content-Type: application/json

{ "sesion_id": "consola-5f2c9a1b", "mensaje": "Confirmo", "cotizacion_origen_id": null }
```

```json
{
  "texto": "Listo, generé la cotización. Puedes descargar el PDF.",
  "herramientas": ["generar_pdf"],
  "precio": null,
  "margen": null,
  "pdf": { "cotizacion_id": "…", "folio": "PCN-DG-25092026-001", "pdf_url": "/pdfs/PCN-DG-25092026-001.pdf", "registrada_en_bd": true }
}
```

Ante un fallo del agente o del proveedor: `200 {"texto": "...", "error": true}` (`RespuestaChatError`). El folio tiene el formato `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]` (iniciales del comercial, fecha de Bogotá y consecutivo diario por iniciales; [ADR-011](../specs/decisiones/ADR-011-folio-e-identidad-google-chat.md)). Los folios sin iniciales de la primera implementación se aceptan como heredados.

## 7.6 Límites, CORS y encabezados

| Tema | Valor | Configuración |
|---|---|---|
| Límite de `/api/auth/*` | 30 peticiones por minuto por IP (429 `ErrorWeb`) | `LimiteAuth:*` |
| Límite de `POST /web/chat` | 20 peticiones por minuto por comercial (429) | `LimiteChat:*` |
| Cuerpo de `/web/*` | 64 KB (413 `ErrorWeb`) | `SeguridadHttp:LimiteCuerpoWebBytes` |
| Mensaje de chat | 1 a 8000 caracteres; `sesion_id` `^[A-Za-z0-9_-]{8,64}$` | Contrato |
| CORS | Orígenes exactos; GET, POST y PUT; encabezados `Authorization` y `Content-Type`; sin credenciales; preflight cacheado 10 minutos | `Cors:OrigenesPermitidos` (obligatorio en Production) |
| Encabezados de seguridad | `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`; `Cache-Control: no-store` en `/web/*` y `/pdfs/*`; sin `Server` | Fijo; HSTS lo pone Traefik |

Los límites son por proceso: con N réplicas el límite efectivo es N veces mayor ([10-operacion](10-operacion.md#106-riesgos-y-limitaciones-conocidos)). Detalle de la configuración en [03-infraestructura](03-infraestructura.md#seguridad-http).

## 7.7 Verificación del contrato

- `Cotifly.Api.Tests` lee `openapi.yaml` y recorre el contrato: cada operación está expuesta, ningún `/web/*` queda fuera del contrato, cada una responde 401 sin token, y los cuerpos reales (incluidos los DTOs y los casos de error) cumplen su esquema.
- El CI corre `redocly lint specs/openapi.yaml` (falla con errores; hoy hay 4 warnings aceptados: servidor `localhost` de ejemplo y operaciones de salud sin respuesta 4XX).
- La comparación contra un documento OpenAPI generado por la propia API (pendiente de ADR-010 §1) no se implementó: el recorrido de rutas y la validación de esquemas cubren la misma deriva sin agregar un generador de OpenAPI a la imagen.
- Las pruebas manuales con Postman de los endpoints `/web/*` son parte de HU-003.9 ([qa/paridad-python-dotnet.md](qa/paridad-python-dotnet.md)).
