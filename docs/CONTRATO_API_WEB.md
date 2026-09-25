# Contrato de la API web de Cotifly (HU-004.1)

Contrato congelado que consume el módulo Angular de ClientePro (HU-004, `clientepro-frontend`) y que implementa el backend .NET 10 (HU-003). La fuente de verdad es [`backend-dotnet/specs/openapi.yaml`](../backend-dotnet/specs/openapi.yaml); este documento resume lo que el frontend necesita saber para integrarse sin leer el código.

- Fecha de congelamiento: 2026-09-24
- Issue: #17 (HU-004.1)
- Validación: `redocly lint` sin errores

## 1. Punto de partida

En el backend Python, de los 5 endpoints `/web/*` solo `POST /web/chat` funciona hoy. `/web/login`, `/web/mis-cotizaciones`, `/enviar` y `/seguimiento` responden 501 desde HU-002.2, porque no hay identidad real sin ClientePro. Por eso este contrato no se capturó de un backend en producción. Se reconstruyó así:

- `POST /web/chat`: del código actual (`orquestador/web.py`), sin cambios de forma.
- `mis-cotizaciones`, `enviar`, `seguimiento`: de la última versión funcional antes del 501 (commit `88884ad^`), con los campos renombrados en HU-002.2 (`efectiva`/`nota` → `ganada`/`motivo_rechazo`; `comercial_email` → identidad por token).
- `/web/login` se reemplaza por `POST /api/auth/login-clientepro` (patrón "Login SSO — Cliente Pro").
- Endpoints nuevos pedidos en HU-003.5, HU-003.6, HU-004.9 y HU-010.1.

## 2. Autenticación

Cotifly no tiene login propio. ClientePro autentica a la persona y le pasa su identidad al backend.

```
ClientePro (ya autenticado)
  └─ POST /api/auth/login-clientepro  { idUsuario, nombreUsuario?, roles? }
       └─ 200 { access, refresh, user, debeCambiarPassword:false }
  └─ Authorization: Bearer <access>   en cada llamada a /web/*
  └─ 401 → POST /api/auth/refresh { refresh }  (una sola vez)
       └─ 401 otra vez → volver al login de ClientePro
  └─ Cerrar sesión → POST /api/auth/logout { refresh }  (siempre 204)
```

| Tema | Regla |
|---|---|
| Identidad | El backend revalida `idUsuario` contra el almacén corporativo (`tblSAPUsuariosApp`) en cada login, primero como `NOMBREUSUARIO` y luego como `ID`. El `NOMBREUSUARIO` de esa fila (no el que envía el cliente) ubica o crea al usuario en `UsuariosWeb`. `nombreUsuario` y `roles` del request son informativos y nunca deciden el acceso. |
| Roles | `comercial` (cotiza y ve lo suyo) y `gerente_comercial` (tablero de su regional y políticas de margen). Ambos pueden iniciar sesión. El rol viaja en el claim `rol` del JWT y en `user.rol`. El `gerente_comercial` también puede usar la consola del comercial (cotiza y ve lo suyo); un `comercial` en `/web/admin/*` recibe 403. |
| Access token | JWT HS256, 60 minutos. Claims: `sub` (UUID de `UsuariosWeb`), `jti`, `username`, `rol`. |
| Refresh token | Opaco, 30 días, se rota en cada uso. Reutilizar uno ya rotado responde 401. |
| Almacenamiento en el cliente | Guardar `access` y `refresh` fuera de logs y de la URL. |
| Primer ingreso | Si `GET /web/usuario/telefono` devuelve `telefono: null`, pedir el teléfono con el modal obligatorio de HU-004.3 y guardarlo con `PUT /web/usuario/telefono`. En la práctica equivale a "la fila se acaba de crear": el teléfono solo se captura en ese modal. |
| Errores de login | Siempre el mismo 401 `{"error":"Credenciales inválidas"}`, sin importar la causa (anti-enumeración). |
| Menú vs login | Ambos roles inician sesión. La regla de HU-004 "solo el comercial ve Cotizador" es del menú, no del login. |

**Auth actual en Python, que se reemplaza:** `POST /web/login {email, nombre}` aceptaba cualquier correo `@boterosoto.com.co` sin contraseña y devolvía `{token, email, nombre}` con un token HMAC de 12 horas. Está desactivado (501) desde HU-002.2.

## 3. Endpoints

| Método y ruta | Rol | Resumen |
|---|---|---|
| `POST /api/auth/login-clientepro` | público | Emite la sesión |
| `POST /api/auth/refresh` | público | Rota el refresh token (429 si se pasa el límite de `/api/auth/*`) |
| `POST /api/auth/logout` | cualquiera | Revoca la sesión, siempre 204 (salvo 429 por el límite de `/api/auth/*`) |
| `POST /web/chat` | ambos | Turno del agente cotizador |
| `GET /web/mis-cotizaciones` | ambos | Cotizaciones propias (máx. 100) |
| `POST /web/cotizaciones/{folio}/enviar` | ambos | Envía la cotización por correo con el PDF. 503 `ErrorNegocio` `CORREO_DESHABILITADO` si el correo está deshabilitado en el servidor |
| `POST /web/cotizaciones/{folio}/correo/vista-previa` | ambos | Asunto, cuerpo y destinatarios sin enviar |
| `POST /web/cotizaciones/{folio}/seguimiento` | ambos | Marca ganada o perdida |
| `GET /web/cotizaciones/{id}/nueva-version` | ambos | Datos para prellenar una nueva versión |
| `GET /web/clientes/{nit}/destinatarios-previos` | ambos | `para`/`cc` del último envío a ese cliente |
| `GET` / `PUT /web/usuario/telefono` | ambos | Teléfono que se imprime en el PDF |
| `GET /web/admin/lineas-negocio` | gerente_comercial | Políticas de margen |
| `PUT /web/admin/lineas-negocio/{id}` | gerente_comercial | Edita margen, método y estado |
| `GET /web/metricas/actividad` | ambos | Actividad por comercial (**borrador**, se define en HU-010.1) |
| `GET /web/metricas/ajustes-margen` | ambos | Ajustes de margen por comercial (**borrador**, se define en HU-010.1) |
| `GET /pdfs/{archivo}` | dueño o gerente de su regional | Descarga del PDF. Exige `Authorization`, así que se baja con HttpClient como blob, no con un `<a href>`. El parámetro `?t=` es solo para el enlace firmado de Google Chat (ADR-011); el frontend no lo usa |

Todos los campos de `/web/*` van en snake_case, incluidos los de los endpoints nuevos, en la respuesta **y en el cuerpo de la petición** (`motivo_rechazo`, `metodo_calculo`, `is_active`; un `motivoRechazo` se ignora). Los campos `nullable` siempre vienen, con `null` si no tienen valor. Un número enviado como texto (`"margen": "0.2"`) es un cuerpo inválido (400).

"Ambos" = `comercial` o `gerente_comercial`; cada uno ve solo sus cotizaciones. En las métricas el comercial ve su fila y el gerente las de los comerciales de su regional (`UsuariosWeb.regional`; si la del gerente aún no se conoce, solo la suya). El gerente puede descargar los PDF de los comerciales de su regional; si su regional aún no se conoce, solo los suyos (fail-closed).

**Folio** (ADR-011, aprobado el 2026-09-25, con iniciales desde el mismo día): lo genera siempre el servidor, `PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`: NIT solo con dígitos cuando la cotización lo tiene, dos letras con las iniciales del comercial (primer nombre + primer apellido, sin tildes; p. ej. "Duban Guerra Castro" → `DG`), fecha de Bogotá, `NNN` consecutivo diario de esas iniciales (`001`…`999`; dos comerciales con las mismas iniciales comparten la secuencia) y `-vN` solo en versiones nuevas (sobre el folio de la v1). Ejemplos: `PCN-DG-25092026-001`, `PCN-9000472281-DG-25092026-002`, `PCN-9000472281-DG-25092026-002-v2`. Máximo 50 caracteres. El frontend no debe asumir el formato para nada distinto de mostrarlo.

### Reservados para la próxima versión (HU-005)

No existen todavía; se agregarán al spec cuando se implemente la integración con Nexo:

| Endpoint previsto | HU |
|---|---|
| Buscador de clientes por nombre o NIT | HU-005.2 |
| Catálogo de ciudades | HU-005.13 |
| Crear prospecto (API Grovity de ClientePro) | HU-005.2 |
| Servicios conexos del costeo | HU-004.4 / HU-011.4 |
| `POST /web/cotizaciones/{folio}/resultado` (reemplaza a `/seguimiento`) | HU-005.14 |

Los esquemas completos de request y response están en el `openapi.yaml`. El spec incluye además el webhook de Google Chat (`POST /chat/events`) y `/health`, `/health/ready`, que no consume el frontend.

## 4. `POST /web/chat`

```json
// request
{ "mensaje": "Cotiza Nacional Bogotá a Medellín tractomula 10000 kg", "sesion_id": "3f2a9c1b7e4d4a6f9b0c2d1e5f6a7b8c" }

// 200 normal
{ "texto": "...", "herramientas": ["obtener_margen", "calcular_precio"],
  "precio": { "precio": 3294798, "costo": 2850000, "margen": 0.135, "metodo": "margen", "utilidad": 444798 },
  "margen": { "margen": 0.135, "origen": "sap_tm", "trq_type": "OPTN", "anomalo": false, "detalle": "Margen SAP TM del cliente para OPTN" },
  "pdf": null }

// 200 con fallo interno controlado
{ "texto": "Ocurrió un error interno procesando tu solicitud. Inténtalo de nuevo.", "error": true }
```

- `precio`, `margen` y `pdf` son `null` si la herramienta no se usó en el turno o terminó en error.
- El formulario de "Nueva cotización" también usa este endpoint, igual que `chat.html`: arma un mensaje con los datos del formulario y lo envía. El progreso de pasos se deriva de `herramientas` y de qué bloques (`margen`, `precio`, `pdf`) vienen con valor. Con el tarifario de Drive retirado, el costo lo aporta el comercial o Nexo (HU-005).
- Un fallo interno responde **200** con `error: true` (igual que Python). El frontend muestra `texto` tal cual. Esto incluye el límite de peticiones del proveedor de IA ("Espera unos segundos y reenvía el mismo mensaje").
- 429 es otra cosa: el límite de peticiones de la propia API de Cotifly (por defecto 20 turnos por minuto y comercial, `LimiteChat`).
- 400 `{"error":"Cuerpo inválido. Revisa: mensaje."}` si `mensaje` está vacío o tiene más de 8000 caracteres.
- 400 `{"error":"sesion_id inválido"}` si no cumple `^[A-Za-z0-9_-]{8,64}$`.
- 404 `{"error":"Sesión no encontrada."}` si el `sesion_id` pertenece a otro usuario.
- `cotizacion_origen_id` (UUID, opcional): "Nueva versión" desde el frontend. El agente parte del cliente, la línea y los números de esa cotización y el PDF sale como `{folio base}-vN`. 404 `{"error":"Cotización no encontrada o no es tuya."}` si no es del comercial; 400 `{"error":"cotizacion_origen_id inválido"}` si no es un UUID. Solo siembra el estado en una sesión nueva o sin cotización en curso (se puede reenviar en cada mensaje sin reiniciar el avance); lo habitual es abrir un `sesion_id` nuevo para la nueva versión.

### Ciclo de vida de `sesion_id`

| Paso | Comportamiento |
|---|---|
| Creación | La genera el cliente: `crypto.randomUUID()` sin guiones. Una por conversación ("Nueva conversación" crea otra). |
| Persistencia | El backend la guarda como `web/{sesion_id}` en `Sesiones`, junto con el estado estructurado de la cotización en curso (cliente, línea, costo, margen). |
| Pertenencia | Queda ligada al usuario del token que la creó. Otro usuario que envíe el mismo `sesion_id` recibe 404. |
| Expiración | Se archiva tras 24 h de inactividad. Un mensaje con una sesión archivada inicia una conversación nueva con el mismo id. |

## 5. Errores

| Forma | Cuándo |
|---|---|
| `{"error": "texto"}` | Autenticación, validación, permisos, recurso inexistente. En Python este formato solo lo usaban el 401, el 400 de `sesion_id` y el 404 de cotización; ver §7 para los que cambian. |
| `{"error": {"codigo", "mensaje", "detalle?"}}` | Error de negocio de una herramienta. Por ejemplo, `/enviar` con `CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO` o `CORREO_ENVIO_FALLIDO`. La lista de códigos está en `ErrorNegocio` del spec. |

| Código | Significado para el frontend |
|---|---|
| 401 | Sesión vencida o revocada: intentar refresh una vez. |
| 403 | El rol no permite la operación (por ejemplo, un comercial en `/web/admin/*`). |
| 404 | No existe o no es del usuario. Son indistinguibles a propósito (mismo cuerpo `{"error":"Cotización no encontrada o no es tuya."}`, también en `/pdfs`). |
| 413 | El cuerpo de una petición a `/web/*` supera 64 KB. |
| 429 | Demasiadas peticiones: reintentar después de unos segundos. |
| 503 | `POST /enviar` con el correo deshabilitado en el servidor: `ErrorNegocio` con `CORREO_DESHABILITADO`. |

## 6. CORS

El backend Python no tenía CORS: `chat.html` funcionaba porque se servía desde el mismo origen. **No se agrega CORS al `orquestador` Python**, porque el módulo Angular va a consumir solo el backend .NET (corte único, HU-003.1). En .NET, los orígenes permitidos se configuran con `Cors__OrigenesPermitidos__0`, `__1`, etc. (origen exacto, `https://host[:puerto]`, sin ruta ni comodines; en producción la API no arranca sin al menos uno). Se permiten los métodos `GET`, `POST` y `PUT` y los headers `Authorization` y `Content-Type`, sin cookies ni credenciales; el preflight se cachea 10 minutos.

**Pendiente:** el dominio real de `clientepro-frontend` en pruebas y en producción. Lo debe confirmar el equipo de frontend.

## 7. Cambios frente al backend Python

| Cambio | Motivo |
|---|---|
| `/web/login` se reemplaza por `/api/auth/login-clientepro` | Login SSO desde ClientePro (HU-004) |
| `GET /pdfs/{archivo}` pasa a exigir sesión | En Python era público y los folios son adivinables |
| `sesion_id` queda ligada al usuario | En Python cualquier token válido podía continuar cualquier sesión |
| `GET /pdfs/{archivo}?t=` (enlace firmado de 15 minutos) | Google Chat no envía `Authorization` al abrir un enlace (ADR-011); solo para el dueño |
| `estado_envio` pasa a `reenviada` en el segundo envío | Bug de Python: siempre escribía `enviada` (HU-003.5). La métrica de enviadas cuenta ambos estados. |
| Seguimiento usa `ganada`/`motivo_rechazo` | Renombrado en HU-002.2 |
| Cuerpo inválido responde 400 `{"error": "..."}` | En Python (FastAPI) era 422 `{"detail": [...]}` |
| 404 de `/pdfs` responde `{"error": "..."}` | En Python era `{"detail": "..."}` |
| Nuevos códigos `CLIENTE_BUSQUEDA_NO_DISPONIBLE` y `HERRAMIENTA_DESCONOCIDA` | Búsqueda de clientes (HU-005.2) y validación del modelo (HU-003.10) |
| Nuevo código `FOLIO_CONSECUTIVO_AGOTADO` | El folio lo genera el servidor con las iniciales del comercial y un consecutivo diario de 3 dígitos por iniciales (ADR-011); el Python lo tomaba del LLM |
| `num_cotizaciones_enviadas` cuenta `enviada` y `reenviada` | La vista del 0001 contaba solo `enviada`; la corrige el script `0003` |
