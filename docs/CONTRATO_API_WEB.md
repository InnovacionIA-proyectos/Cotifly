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
| Identidad | `idUsuario` es el único campo que decide quién es. El backend lo revalida contra el almacén corporativo (`tblSAPUsuariosApp`) en cada login. `roles` que envíe el frontend es informativo y nunca decide el acceso. |
| Roles | `comercial` (cotiza y ve lo suyo) y `gerente_comercial` (tablero de su regional y políticas de margen). El rol viaja en el claim `rol` del JWT y en `user.rol`. |
| Access token | JWT HS256, 60 minutos. Claims: `sub` (UUID de `UsuariosWeb`), `jti`, `username`, `rol`, `email`. |
| Refresh token | Opaco, 30 días, se rota en cada uso. Reutilizar uno ya rotado responde 401. |
| Almacenamiento en el cliente | Guardar `access` y `refresh` fuera de logs y de la URL. |
| Primer ingreso | Si `GET /web/usuario/telefono` devuelve `telefono: null`, pedir el teléfono con el modal obligatorio de HU-004.3 y guardarlo con `PUT /web/usuario/telefono`. |
| Errores de login | Siempre el mismo 401 `{"error":"Credenciales inválidas"}`, sin importar la causa (anti-enumeración). |

## 3. Endpoints

| Método y ruta | Rol | Resumen |
|---|---|---|
| `POST /api/auth/login-clientepro` | público | Emite la sesión |
| `POST /api/auth/refresh` | público | Rota el refresh token |
| `POST /api/auth/logout` | cualquiera | Revoca la sesión, siempre 204 |
| `POST /web/chat` | comercial | Turno del agente cotizador |
| `GET /web/mis-cotizaciones` | comercial | Cotizaciones propias (máx. 100) |
| `POST /web/cotizaciones/{folio}/enviar` | comercial | Envía la cotización por correo con el PDF |
| `POST /web/cotizaciones/{folio}/correo/vista-previa` | comercial | Asunto, cuerpo y destinatarios sin enviar |
| `POST /web/cotizaciones/{folio}/seguimiento` | comercial | Marca ganada o perdida |
| `GET /web/cotizaciones/{id}/nueva-version` | comercial | Datos para prellenar una nueva versión |
| `GET /web/clientes/{nit}/destinatarios-previos` | comercial | `para`/`cc` del último envío a ese cliente |
| `GET` / `PUT /web/usuario/telefono` | comercial | Teléfono que se imprime en el PDF |
| `GET /web/admin/lineas-negocio` | gerente_comercial | Políticas de margen |
| `PUT /web/admin/lineas-negocio/{id}` | gerente_comercial | Edita margen, método y estado |
| `GET /web/metricas/actividad` | ambos | Actividad por comercial |
| `GET /web/metricas/ajustes-margen` | ambos | Ajustes de margen por comercial |
| `GET /pdfs/{archivo}` | dueño o gerente | Descarga del PDF (ahora requiere sesión) |

Los esquemas completos de request y response están en el `openapi.yaml`.

## 4. `POST /web/chat`

```json
// request
{ "mensaje": "Cotiza Nacional Bogotá a Medellín tractomula 10000 kg", "sesion_id": "3f2a9c1b7e4d4a6f9b0c2d1e5f6a7b8c" }

// 200 normal
{ "texto": "...", "herramientas": ["buscar_cliente", "obtener_margen", "calcular_precio"],
  "precio": { "precio": 3294798, "costo": 2850000, "margen": 0.135, "metodo": "margen", "utilidad": 444798 },
  "margen": { "margen": 0.135, "origen": "sap_tm", "trq_type": "OPTN", "anomalo": false, "detalle": null },
  "pdf": null }

// 200 con fallo interno controlado
{ "texto": "Ocurrió un error interno procesando tu solicitud. Inténtalo de nuevo.", "error": true }
```

- `precio`, `margen` y `pdf` son `null` si la herramienta no se usó en el turno o terminó en error.
- Un fallo interno responde **200** con `error: true` (igual que Python). El frontend muestra `texto` tal cual.
- 400 `{"error":"sesion_id inválido"}` si no cumple `^[A-Za-z0-9_-]{8,64}$`.

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
| `{"error": "texto"}` | Autenticación, validación, permisos, recurso inexistente. Mismo formato que Python. |
| `{"error": {"codigo", "mensaje", "detalle?"}}` | Error de negocio de una herramienta. Por ejemplo, `/enviar` con `CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO` o `CORREO_ENVIO_FALLIDO`. La lista de códigos está en `ErrorNegocio` del spec. |

| Código | Significado para el frontend |
|---|---|
| 401 | Sesión vencida o revocada: intentar refresh una vez. |
| 403 | El rol no permite la operación (por ejemplo, un comercial en `/web/admin/*`). |
| 404 | No existe o no es del usuario. Son indistinguibles a propósito. |
| 429 | Demasiadas peticiones: reintentar después de unos segundos. |

## 6. CORS

El backend Python no tenía CORS: `chat.html` funcionaba porque se servía desde el mismo origen. En .NET, los orígenes permitidos se configuran con `Cors__OrigenesPermitidos__0`, `__1`, etc. Se permiten los métodos `GET`, `POST` y `PUT` y los headers `Authorization` y `Content-Type`, sin cookies.

**Pendiente:** el dominio real de `clientepro-frontend` en pruebas y en producción. Lo debe confirmar el equipo de frontend.

## 7. Cambios frente al backend Python

| Cambio | Motivo |
|---|---|
| `/web/login` se reemplaza por `/api/auth/login-clientepro` | Login SSO desde ClientePro (HU-004) |
| `GET /pdfs/{archivo}` pasa a exigir sesión | En Python era público y los folios son adivinables |
| `sesion_id` queda ligada al usuario | En Python cualquier token válido podía continuar cualquier sesión |
| `estado_envio` pasa a `reenviada` en el segundo envío | Bug de Python: siempre escribía `enviada` (HU-003.5) |
| Seguimiento usa `ganada`/`motivo_rechazo` | Renombrado en HU-002.2 |
