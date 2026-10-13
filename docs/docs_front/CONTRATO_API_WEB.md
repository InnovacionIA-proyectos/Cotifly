# Contrato de la API web de Cotifly (HU-004.1)

Contrato congelado que consume el módulo Angular de ClientePro (HU-004, `clientepro-frontend`) y que implementa el backend .NET 10 (HU-003). La fuente de verdad es [`backend-dotnet/specs/openapi.yaml`](../../backend-dotnet/specs/openapi.yaml); este documento resume lo que el frontend necesita saber para integrarse sin leer el código.

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
| `POST /web/chat` | ambos | Turno del agente cotizador (ya no crea cotizaciones, ADR-018) |
| `POST /web/solicitudes-costeo` | ambos | **Endpoint vigente para crear una cotización** (una línea): formulario ampliado, validación por código de catálogo y envío a costeo en Nexo. Ver §3.2 |
| `GET /web/catalogos-flete` | ambos | Tipos de vehículo, carrocerías y clases de viaje del formulario (catálogos maestros de Nexo). Ver §3.2 |
| `GET /web/mis-cotizaciones` | ambos | Cotizaciones propias (máx. 100) |
| `POST /web/cotizaciones/{folio}/enviar` | ambos | Envía la cotización por correo con el PDF. 503 `ErrorNegocio` `CORREO_DESHABILITADO` si el correo está deshabilitado en el servidor |
| `POST /web/cotizaciones/{folio}/correo/vista-previa` | ambos | Asunto, cuerpo y destinatarios sin enviar |
| `POST /web/cotizaciones/{folio}/seguimiento` | ambos | Marca ganada o perdida **solo** cotizaciones fuera de Nexo (`estado_costeo` `null`, `descartada` o `costeo_vencido`). Las de Nexo responden 409 `USAR_RESULTADO_NEXO`, con `detalle` = `{metodo, endpoint, requiere_if_match}` (HU-021.13) |
| `POST /web/cotizaciones/{folio}/resultado` | ambos (dueño) | Marca ganada o perdida las cotizaciones de Nexo (primero en Nexo y, si responde 200, en Cotifly). Lleva `If-Match` (obligatorio en la perdida sin costeo: 428 si falta). `motivo` máximo 100 caracteres en una perdida; en una ganada no se valida. `motivo_rechazo` queda `"No aplica"` en una ganada (HU-021.13, alimenta HU-019.1) |
| `GET /web/cotizaciones/{id}/nueva-version` | ambos | Datos para prellenar una nueva versión |
| `GET /web/clientes/{nit}/destinatarios-previos` | ambos | `para`/`cc` del último envío a ese cliente |
| `GET` / `PUT /web/usuario/telefono` | ambos | Teléfono que se imprime en el PDF |
| `GET /web/admin/lineas-negocio` | gerente_comercial | Políticas de margen |
| `PUT /web/admin/lineas-negocio/{id}` | gerente_comercial | Edita margen, método y estado |
| `GET /web/metricas/comercial` | ambos | Tablero del comercial (HU-010.2): indicadores y serie mensual de sus cotizaciones. Ver §3.1 |
| `GET /web/metricas/regional` | gerente_comercial | Tablero del gerente (HU-010.3): su regional, filtrable por `usuario_id`, con lista de comerciales, serie mensual y concentración por ciudad. Ver §3.1 |
| `GET /web/metricas/actividad` | ambos | Actividad por comercial (**borrador obsoleto**: usar `/web/metricas/comercial` y `/regional`) |
| `GET /web/metricas/ajustes-margen` | ambos | Ajustes de margen por comercial (**borrador obsoleto**) |
| `GET /pdfs/{archivo}` | dueño o gerente de su regional | Descarga del PDF. Exige `Authorization`, así que se baja con HttpClient como blob, no con un `<a href>`. El enlace firmado `?t=` (ADR-011) se retiró en #105 junto con Google Chat |

Todos los campos de `/web/*` van en snake_case, incluidos los de los endpoints nuevos, en la respuesta **y en el cuerpo de la petición** (`motivo_rechazo`, `metodo_calculo`, `is_active`; un `motivoRechazo` se ignora). Los campos `nullable` siempre vienen, con `null` si no tienen valor. Un número enviado como texto (`"margen": "0.2"`) es un cuerpo inválido (400).

"Ambos" = `comercial` o `gerente_comercial`; cada uno ve solo sus cotizaciones. En las métricas del borrador (`/actividad`, `/ajustes-margen`) el comercial ve su fila y el gerente las de los comerciales de su regional (`UsuariosWeb.regional`; si la del gerente aún no se conoce, solo la suya). El gerente puede descargar los PDF de los comerciales de su regional; si su regional aún no se conoce, solo los suyos (fail-closed).

**Folio** (ADR-011, aprobado el 2026-09-25, con iniciales desde el mismo día): lo genera siempre el servidor, `PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`: NIT solo con dígitos cuando la cotización lo tiene, dos letras con las iniciales del comercial (primer nombre + primer apellido, sin tildes; p. ej. "Duban Guerra Castro" → `DG`), fecha de Bogotá, `NNN` consecutivo diario de esas iniciales (`001`…`999`; dos comerciales con las mismas iniciales comparten la secuencia) y `-vN` solo en versiones nuevas (sobre el folio de la v1). Ejemplos: `PCN-DG-25092026-001`, `PCN-9000472281-DG-25092026-002`, `PCN-9000472281-DG-25092026-002-v2`. Máximo 50 caracteres. El frontend no debe asumir el formato para nada distinto de mostrarlo.

### 3.1 Métricas del tablero (HU-010.1, #45)

Decisiones completas en `backend-dotnet/specs/decisiones/ADR-016-metricas-agregadas-y-regional.md`; esquemas y ejemplos en el tag `metricas` del spec.

**Parámetros.** `desde` y `hasta` son opcionales (`AAAA-MM-DD`, fechas de Bogotá, ambas incluidas). Por defecto van del 1 de enero del año en curso a hoy; máximo 3 años. En `/regional`, `usuario_id` es opcional (UUID de un comercial de la regional). Fechas mal formadas, `hasta < desde` o un `usuario_id` que no es UUID: 400.

**Quién ve qué.** El rol sale del token de la sesión (claim `rol`, emitido en el login SSO con la verificación doble de ClientePro); no se lee de ninguna columna.
- `/comercial`: las cotizaciones propias (también para un gerente, que cotiza).
- `/regional`: solo `gerente_comercial` (un comercial recibe 403). Siempre la regional del gerente autenticado; no se puede pedir otra.
  - `comerciales` trae a todos los usuarios de la regional, **incluido el gerente**, cada uno con su `rol` (`comercial`, `gerente_comercial` o `null`) y sus indicadores, para el selector.
  - Las cotizaciones del gerente cuentan en el agregado de la regional.
  - `usuario_id` recalcula `indicadores`, `serie_mensual` y `concentracion_ciudades` para ese comercial. Si no es de la regional: 404 `{"error":"Comercial no encontrado en tu regional."}`.
- Gerente sin regional: 409 `ErrorNegocio` `REGIONAL_NO_ASIGNADA` (el frontend muestra "tu usuario no tiene regional asignada"). Fuente de la regional caída: 503 `REGIONAL_NO_DISPONIBLE` (reintentar más tarde). La regional resuelta se reutiliza 60 s, así que cambiar el selector no vuelve a consultarla.

**Qué se cuenta: una fila por cadena de versiones.** La cadena son las filas del mismo comercial con el mismo folio base (el folio sin `-vN`); cuenta una sola vez, en cualquier estado (lo mismo que lista "Mis cotizaciones").
- Valores (precio, costo, margen, moneda, ciudades, deadline): los de la **versión vigente**, la de mayor `version`.
- Periodo y mes: la **primera creación** de la cadena. Ejemplo: `PCN-DG-10082026-001` se crea el 10 de agosto por 1.000.000 y se rehace el 3 de septiembre (`-v2`) por 1.100.000. Cuenta una cotización en **agosto** con valor 1.100.000, y septiembre no la ve. Los periodos pasados no cambian al rehacer.
- Resultado: el **más reciente de toda la cadena** (mayor `marcadoAt`; si no hay fecha, la versión mayor). Una v1 ganada con una v2 sin marcar cuenta como ganada.
- `versiones` informa cuántas filas tienen esas cadenas.

**Definiciones** (para recalcular a mano sobre `dbo_cotifly`, HU-010.4):

| Campo | Definición |
|---|---|
| `cantidad` | Cadenas del periodo |
| `versiones` | Filas de esas cadenas (todas las versiones) |
| `ganadas` / `perdidas` | Resultado ganada / perdida de la cadena |
| `sin_resultado` | Con precio y sin resultado |
| `pendientes_por_cotizar` | Sin precio y sin resultado |
| `conversion` | ganadas / cantidad (definición del backlog); `null` si cantidad = 0 |
| `conversion_sobre_cerradas` | ganadas / (ganadas + perdidas); `null` si no hay cerradas |
| `margen_promedio` | Promedio simple del margen de cada cotización (0.15 = 15 %); el de una cotización es el de sus rutas ponderado por costo |
| `margen_ponderado` | Por ruta, sobre el precio: SUM(precio de la ruta en COP − `costoFinal`) / SUM(precio de la ruta en COP) de todas las rutas con costo > 0 y precio > 0 (HU-021.10) |
| `costeo_total` / `costeo_ganadas` | SUM(`costo`) en COP, de todas / de las ganadas |
| `valor_cotizado_cop` | SUM(`valor_cop`) |
| `valor_ganado_cop` | SUM(`valor_cop`) de las ganadas: valor cotizado de lo ganado, **no dinero recaudado**. Rotularlo "Valor ganado (COP)", también en el mapa |
| `ganancias` | SUM(`valor_cop` − `costo`) de las ganadas con precio y costo |
| `cotizaciones_usd` | Cotizaciones en moneda distinta de COP |
| `tiempo_promedio_cotizacion_horas` | Promedio de `costeoListoAt` − `createdAt` (versión vigente), en horas; sin las filas incoherentes (`costeoListoAt` antes de `createdAt`) |
| `desviacion_promedio_margen` / `desviacion_promedio_margen_abs` / `ajustes_margen` | Promedio de `MargenAjustes.diferencia` con signo / en valor absoluto, y cantidad de ajustes de la versión vigente |
| `deadline_vencidas` | Sin resultado y con `deadline` ya vencido; `null` hasta que se aplique el script 0004 |
| `lineas_negocio_activas` (solo `/regional`) | `MargenesPolitica.isActive = 1` |

`valor_cop` es `precioFinal` en COP; en USD, `round(precioFinal × trmAplicada, 2)`. No es el `precio_cop` de la TRM (ADR-014, el precio en pesos antes de convertir), que se recalcula del costo y el margen y puede diferir unos pesos. El costo ya está en COP y nunca se convierte. Montos con 2 decimales; fracciones de 0 a 1 con 4 decimales; redondeo half-to-even.

Identidades para QA:
- `cantidad = ganadas + perdidas + sin_resultado + pendientes_por_cotizar`, siempre.
- `ganancias = valor_ganado_cop − costeo_ganadas`, cuando todas las ganadas tienen precio y costo y ninguna en USD tiene TRM 0.

**Gráficas.**
- `serie_mensual` trae un punto por mes del periodo (`AAAA-MM`, también los meses vacíos), con cantidad, ganadas, perdidas, valor cotizado y valor ganado.
- `concentracion_ciudades.por_destino` (municipio de destino, el que usa el mapa) y `por_origen` traen cantidad, ganadas y valor por municipio, con el texto del catálogo de Nexo.
  - `disponible` es `false` hasta que se aplique el script 0004 (columnas `origen`/`destino`).
  - Las cotizaciones sin ciudad cuentan en `sin_destino`/`sin_origen`.
  - Una solicitud de varias rutas cuenta una vez, con la ciudad de su ruta 1.

**Rutas (HU-021.10, #137; decisión 8).** Los conteos (`cantidad`, `ganadas`, `perdidas`…) son por cotización: una cotización con 3 rutas de 3 líneas de negocio cuenta 1. Los valores salen de sus rutas: `costo` = suma del `costoFinal` de las rutas (ya incluye los servicios conexos). `por_linea_negocio[]` (en `/comercial` y `/regional`) abre por línea de negocio **por ruta**: `cotizaciones`/`ganadas`/`perdidas` son cotizaciones distintas con al menos una ruta de la línea (la suma de la apertura puede superar `cantidad`), `rutas`, `costeo_total`/`costeo_ganadas`, `valor_cotizado_cop`/`valor_ganado_cop` (precio de cada ruta en COP, USD × TRM redondeado por ruta), `ganancias`, `margen_ponderado`, `ajustes_margen` y `desviacion_promedio_margen` (ajustes de esas rutas). Esquema `MetricaLineaNegocio`; fórmulas en ADR-016 §10.

**Estado vacío.** Un comercial sin cotizaciones recibe 200 con ceros, `null` en los promedios, la serie en 0 y `por_linea_negocio` vacía.

**Todavía no incluido:**
- El "score de urgencia" de Nexo no está en el contrato. Se agregará como campo nuevo de `indicadores` (cambio compatible) cuando Nexo publique su contrato.

### 3.2 Crear una cotización: `POST /web/solicitudes-costeo` (HU-005.5, HU-013.10)

> **HU-021 (#132 a #138): el cuerpo pasa a `rutas[]`.** Esta sección describe el formato plano anterior, que se mantiene
> (`deprecated`) hasta que salga HU-021-FE. El contrato vigente, con ejemplos y los cambios que rompen, está en la **sección 8**.

Desde ADR-018 es el único camino para crear una cotización: el formulario manda una o varias rutas (para un pliego en Excel está
`POST /web/licitaciones`). El cuerpo es `SolicitudCosteoWeb` del spec; responde `201 CotizacionResumen` aunque
Nexo falle (la cotización queda en `error_envio` con el motivo y se reintenta con `POST /web/cotizaciones/{folio}/costeo/reintentar`).

Reglas para el frontend (HU-013.10, #106):

- **`tipo_vehiculo` y `carroceria` ya no se envían** (decisión 7 de HU-021: los define el cubicador o la Mesa de Costeo de Nexo;
  si llegan, se ignoran). Lo que sigue aplica a ellos solo con la bandera del servidor `Captura:AceptarVehiculoCliente`.
- **Solo códigos de catálogo** en `tipo_vehiculo`, `carroceria` y `clase_viaje` (el `valor` de `GET /web/catalogos-flete`,
  nunca la `etiqueta`), `tipologia` (enum de 7 valores) y `clase_contenedor` (`CNT20.IT`, `CNT20.RE`, `CNT20.ST`, `CNT40.HC`,
  `CNT40.RE` y la lista provisional `CNT20.FT`, `CNT20.OP`, `CNT40.FT`, `CNT40.OP`). Otro texto es 400
  `SOLICITUD_COSTEO_INVALIDA` con el campo en el mensaje. Si los catálogos de Nexo no están disponibles (ni su copia en
  memoria), 503 `CATALOGOS_FLETE_NO_DISPONIBLE` y no se guarda nada. Sin Nexo configurado en el servidor, un formulario con
  `tipo_vehiculo` o `clase_viaje` siempre recibe ese 503.
- **Condicionales:** `clase_contenedor` es obligatoria con `clase_viaje = Contenedor` y no se admite con otra clase;
  `valor_aprovisionamiento` solo con una `ciudad_levante_contenedor` distinta del origen.
- **`deadline`:** ISO 8601. Sin zona se toma en hora de Bogotá; una fecha sola (`2026-10-10`) es el inicio de ese día en
  Bogotá (`2026-10-10T00:00:00-05:00`). Para otra hora, enviarla con `-05:00`.
- **Campos nuevos:** `notas` (nota para la Mesa de Nexo, máx. 2000), `origen_municipio_id` y `destino_municipio_id` (el `id`
  de `GET /web/municipios`; obligatorios desde HU-021, decisión 2).
- **Retirados (HU-021, decisión 1):** `valor_estimado` y `referencia` ya no están en el contrato; si llegan, se ignoran.
- `GET /web/catalogos-flete` responde 503 `CATALOGOS_FLETE_NO_DISPONIBLE` solo si Nexo no responde y no hay una copia de los
  catálogos de menos de 24 horas: en ese caso, deshabilitar los selects y ofrecer reintento (no hay texto libre).

### Reservados para la próxima versión (HU-005)

No existen todavía; se agregarán al spec cuando se implemente la integración con Nexo:

| Endpoint previsto | HU |
|---|---|
| Buscador de clientes por nombre o NIT | HU-005.2 |
| Catálogo de ciudades | HU-005.13 |
| Crear prospecto (API Grovity de ClientePro) | HU-005.2 |
| Servicios conexos del costeo | HU-004.4 / HU-011.4 |
| `POST /web/cotizaciones/{folio}/resultado` (reemplaza a `/seguimiento`) | HU-005.14 |

Los esquemas completos de request y response están en el `openapi.yaml`. El spec incluye además `/health` y `/health/ready`, que no consume el frontend. El webhook de Google Chat (`POST /chat/events`) se retiró en #105 (HU-013.9).

## 4. `POST /web/chat`

Desde HU-013.6 (#102) el chat es el **asistente de una cotización ya creada** y desde HU-013.9 (#105) el agente cotizador legado
no existe: las cotizaciones se crean solo con el formulario (`POST /web/solicitudes-costeo`, 3.2) y una versión nueva sale de
`POST /web/cotizaciones/{folio}/costeo/re-solicitar` con `nueva_version: true` o con cambios de carga. Detalle completo en
`docs/GUIA_FRONTEND_COTIFLY.md` (secciones 5.3 y 10, "Asistente por folio").

```json
// request
{ "mensaje": "Súbele el margen al 18 %", "sesion_id": "3f2a9c1b7e4d4a6f9b0c2d1e5f6a7b8c", "folio": "PCN-900555111-DG-05102026-001" }

// 200 normal: texto sin montos y, si la hubo, la acción propuesta que el frontend confirma con el endpoint determinista
{ "texto": "...", "herramientas": ["consultar_cotizacion", "proponer_ajuste_margen"], "precio": null, "margen": null, "pdf": null,
  "errores": [], "accion_propuesta": { "tipo": "ajustar_margen", "metodo": "PUT", "endpoint": "/web/cotizaciones/.../costeo/margen",
  "cuerpo": { "margen": 0.18, "justificacion": null }, "resumen": "...", "row_version": "7" } }

// 200 con fallo interno controlado
{ "texto": "Ocurrió un error interno procesando tu solicitud. Inténtalo de nuevo.", "error": true }
```

- `folio` es obligatorio: sin él (o vacío), 400 `ErrorNegocio` `FOLIO_REQUERIDO`; con más de 50 caracteres, 400 `{"error":"folio inválido"}`; ajeno o inexistente, 404 `{"error":"Cotización no encontrada o no es tuya."}`.
- `precio`, `margen` y `pdf` son **obsoletos** y siempre `null`. `cotizacion_origen_id` y `moneda` del request son **obsoletos**: se aceptan y se ignoran (ya no producen 400 ni 404).
- `errores` (`[{herramienta, codigo}]`): errores de negocio de las herramientas del turno, con los códigos de `ErrorNegocio`.
- Un fallo interno responde **200** con `error: true`. El frontend muestra `texto` tal cual. Esto incluye el límite de peticiones del proveedor de IA.
- 429 es otra cosa: el límite de peticiones de la propia API de Cotifly (por defecto 20 turnos por minuto y comercial, `LimiteChat`).
- 400 `{"error":"Cuerpo inválido. Revisa: mensaje."}` si `mensaje` está vacío o tiene más de 8000 caracteres.
- 400 `{"error":"sesion_id inválido"}` si no cumple `^[A-Za-z0-9_-]{8,64}$`.
- 404 `{"error":"Sesión no encontrada."}` si el `sesion_id` pertenece a otro usuario.

### Ciclo de vida de `sesion_id`

| Paso | Comportamiento |
|---|---|
| Creación | La genera el cliente: `crypto.randomUUID()` sin guiones. Una por conversación ("Nueva conversación" crea otra). |
| Persistencia | El backend la guarda en `Sesiones` como `spaceId = web/{sesion_id}` y `threadId = web/{sesion_id}/{FOLIO}` (una conversación por folio), con el folio de la cotización como estado. |
| Pertenencia | Queda ligada al usuario del token que la creó. Otro usuario que envíe el mismo `sesion_id` recibe 404. |
| Expiración | Se archiva tras 24 h de inactividad. Un mensaje con una sesión archivada inicia una conversación nueva con el mismo id. |

## 5. Errores

| Forma | Cuándo |
|---|---|
| `{"error": "texto"}` | Autenticación, validación, permisos, recurso inexistente. En Python este formato solo lo usaban el 401, el 400 de `sesion_id` y el 404 de cotización; ver §7 para los que cambian. |
| `{"error": {"codigo", "mensaje", "detalle?"}}` | Error de negocio de una herramienta. Por ejemplo, `/enviar` con `CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO` o `CORREO_ENVIO_FALLIDO`. La lista de códigos está en `ErrorNegocio` del spec. En `/web/chat` los errores de las herramientas no son un HTTP de error: llegan en `errores` de la respuesta 200 (ej. `TRM_NO_DISPONIBLE`, ninguna fuente de la TRM respondió). |

| Código | Significado para el frontend |
|---|---|
| 401 | Sesión vencida o revocada: intentar refresh una vez. |
| 403 | El rol no permite la operación (por ejemplo, un comercial en `/web/admin/*`). |
| 404 | No existe o no es del usuario. Son indistinguibles a propósito (mismo cuerpo `{"error":"Cotización no encontrada o no es tuya."}`, también en `/pdfs`). |
| 413 | El cuerpo de una petición a `/web/*` supera 64 KB. |
| 429 | Demasiadas peticiones: reintentar después de unos segundos. |
| 409 | `GET /web/metricas/regional` de un gerente sin regional: `ErrorNegocio` con `REGIONAL_NO_ASIGNADA`. |
| 503 | `POST /enviar` con el correo deshabilitado en el servidor: `ErrorNegocio` con `CORREO_DESHABILITADO`. `GET /web/metricas/regional` con la fuente de la regional caída: `REGIONAL_NO_DISPONIBLE`. |

## 6. CORS

El backend Python no tenía CORS: `chat.html` funcionaba porque se servía desde el mismo origen. **No se agrega CORS al `orquestador` Python**, porque el módulo Angular va a consumir solo el backend .NET (corte único, HU-003.1). En .NET, los orígenes permitidos se configuran con `Cors__OrigenesPermitidos__0`, `__1`, etc. (origen exacto, `https://host[:puerto]`, sin ruta ni comodines; en producción la API no arranca sin al menos uno). Se permiten los métodos `GET`, `POST` y `PUT` y los headers `Authorization` y `Content-Type`, sin cookies ni credenciales; el preflight se cachea 10 minutos.

**Pendiente:** el dominio real de `clientepro-frontend` en pruebas y en producción. Lo debe confirmar el equipo de frontend.

## 7. Cambios frente al backend Python

| Cambio | Motivo |
|---|---|
| `/web/login` se reemplaza por `/api/auth/login-clientepro` | Login SSO desde ClientePro (HU-004) |
| `GET /pdfs/{archivo}` pasa a exigir sesión | En Python era público y los folios son adivinables |
| `sesion_id` queda ligada al usuario | En Python cualquier token válido podía continuar cualquier sesión |
| `estado_envio` pasa a `reenviada` en el segundo envío | Bug de Python: siempre escribía `enviada` (HU-003.5). La métrica de enviadas cuenta ambos estados. |
| Seguimiento usa `ganada`/`motivo_rechazo` | Renombrado en HU-002.2 |
| Cuerpo inválido responde 400 `{"error": "..."}` | En Python (FastAPI) era 422 `{"detail": [...]}` |
| 404 de `/pdfs` responde `{"error": "..."}` | En Python era `{"detail": "..."}` |
| Nuevos códigos `CLIENTE_BUSQUEDA_NO_DISPONIBLE` y `HERRAMIENTA_DESCONOCIDA` | Búsqueda de clientes (HU-005.2) y validación del modelo (HU-003.10) |
| Nuevo código `FOLIO_CONSECUTIVO_AGOTADO` | El folio lo genera el servidor con las iniciales del comercial y un consecutivo diario de 3 dígitos por iniciales (ADR-011); el Python lo tomaba del LLM |
| `num_cotizaciones_enviadas` cuenta `enviada` y `reenviada` | La vista del 0001 contaba solo `enviada`; la corrige el script `0003` |

## 8. Contrato por rutas (HU-021, #130 a #140; cerrado en HU-021.11, #138)

Desde HU-021 una cotización es una **solicitud con rutas**, y cada ruta tiene **camiones**, **mercancías** y **servicios conexos**
(ADR-019). Esta sección es el resumen para el equipo del frontend. Los ejemplos JSON cumplen `openapi.yaml`: la prueba
`clsContratoEjemplosDocTests` valida cada bloque marcado con `<!-- esquema: X -->` o `<!-- respuesta: METODO /ruta ESTADO -->`
contra el contrato, así que un ejemplo desactualizado rompe la compilación de pruebas.

### 8.1 Cambios que rompen (leer primero)

| # | Cambio | Qué hacer en el frontend |
|---|---|---|
| 1 | **`precio_final` incluye los servicios conexos.** Es la suma de `rutas[].precio_final_linea`, y cada ruta es fletes + retorno + levante + conexos, con su margen. | No sumar el `precio` de `servicios_conexos`: es el desglose. El PDF muestra la tarifa del transporte y los conexos aparte, y tarifa + conexos = precio de la ruta. |
| 2 | **`POST /web/solicitudes-costeo` recibe `rutas[]`** (hasta 100). Sin `rutas`, el formato plano anterior sigue funcionando como una sola ruta, pero está `deprecated` y **se retira cuando salga HU-021-FE**. | Migrar el formulario a `rutas[]`. No mezclar: `rutas` y un campo de ruta en la raíz es 400 `SOLICITUD_COSTEO_INVALIDA` con `CAMPO_NO_APLICA`. |
| 3 | **`tipo_vehiculo` y `carroceria` se ignoran** (decisión 7: los define el cubicador o la Mesa de Costeo de Nexo). | Quitarlos del formulario y del mapeo del Excel. El vehículo se lee de `rutas[].camiones[]`. Excepción: con la bandera del servidor `Captura:AceptarVehiculoCliente = true` (apagada por defecto) se usan en el camión del respaldo mientras no haya cubicador (HU-021-FE.2). |
| 4 | **`valor_estimado` y `referencia` salen del contrato** (decisión 1), también `referencia` de la vista `Licitacion`. | Dejar de enviarlos y de leerlos. Si se envían, se ignoran sin error (queda un aviso en el log). |
| 5 | **Municipios con id obligatorio.** Origen, destino y las ciudades de devolución y de levante van con su id de `GET /web/municipios` y una copia del nombre. Toda captura consulta el catálogo: si no responde, **503 `MUNICIPIOS_NO_DISPONIBLE`** y nada se guarda. | Enviar siempre el `*_municipio_id`. Ante el 503, ofrecer Reintentar. Ante `MUNICIPIO_AMBIGUO`, mostrar los candidatos y repetir con el id elegido. |
| 6 | **Mercancías con `id_tipo_mercancia` obligatorio** (catálogo del cubicador, `GET /web/tipos-mercancia`) y `nombre_mercancia` (antes `descripcion`). | Usar el desplegable del catálogo. Sin cubicador, el endpoint responde 503 `CUBICADOR_NO_DISPONIBLE`: el formulario sigue con los tipos que ya conoce. |
| 7 | **Margen y conexos por ruta.** `PUT .../costeo/margen` y `PUT`/`POST .../costeo/servicios-conexos` llevan `linea_orden` (o `linea_solicitud_id`). Con varias rutas y sin ruta: **400 `RUTA_REQUERIDA`** con `detalle.rutas`. | Mostrar un selector de ruta y repetir la petición. Los campos de margen de primer nivel de `CosteoCotizacion` son los de la ruta 1 (compatibilidad). |
| 8 | **`servicios_conexos[].estado_nexo`** (`pendiente`, `enviado`, `incierto`, `fallido_definitivo`) y **`rutas[].aviso_conexos_nexo`**. | Mostrar el aviso cuando venga: el comercial o la Mesa deben revisar ese conexo en Nexo. Nunca reintentar desde el frontend. |
| 9 | **`/resultado` en vez de `/seguimiento`.** Una cotización que pasó por Nexo responde 409 `USAR_RESULTADO_NEXO` en `/seguimiento`, y el `detalle` dice a dónde ir: `metodo`, `endpoint` y `requiere_if_match`. | Llamar a `POST /web/cotizaciones/{folio}/resultado` con `If-Match` (el `row_version` del costeo). Obligatorio en la perdida sin costeo (428 sin él). |
| 10 | **`motivo_rechazo` de una ganada es `"No aplica"`** (HU-021.13). | Mostrar el motivo solo en las perdidas. |
| 11 | **"Mis cotizaciones" y la nueva versión por ruta.** `linea_negocio` es la de la ruta 1 o `Varias`, y `margen_aplicado`/`origen_margen`, los de la ruta 1. `DatosNuevaVersion.rutas[]` trae los de cada ruta. | Mostrar `Varias` tal cual. Para el detalle, usar `GET .../costeo` o `rutas[]`. |
| 12 | **`conexos_descartados`** de la re-solicitud es siempre `[]` (`deprecated`; se retira con HU-021-FE). Los conexos se conservan y se avisan en `conexos_conservados`. | Leer `conexos_conservados`. |
| 13 | **Re-solicitar con `datos_carga`** solo aplica a una cotización de **una** ruta (con varias, 409 `ESTADO_NO_PERMITE`). | Con varias rutas, crear la versión con `nueva_version: true` y sin `datos_carga`. |

### 8.2 Códigos de error nuevos de HU-021

| HTTP | Código | Dónde | Detalle |
|---|---|---|---|
| 400 | `SOLICITUD_COSTEO_INVALIDA` | Captura | Ahora con **todos** los errores en `detalle.errores[]` = `{ruta, campo, codigo, mensaje}` (`ruta` base 0, `null` si es de la raíz). |
| 400 (en `detalle.errores`) | `MUNICIPIO_INEXISTENTE`, `MUNICIPIO_AMBIGUO`, `MUNICIPIO_NO_ENCONTRADO` | Captura y licitación | El id no existe; el nombre tiene varios candidatos (el mensaje trae los ids) o ninguno. |
| 400 (en `detalle.errores`) | `TIPO_MERCANCIA_INEXISTENTE` | Captura | `mercancias[i].id_tipo_mercancia` no está en el catálogo del cubicador. |
| 400 (en `detalle.errores`) | `CONEXO_NO_ENCONTRADO`, `CONEXO_DUPLICADO`, `DEMASIADOS_CONEXOS` | Captura | Conexo fuera del catálogo de Nexo, repetido en la ruta o más de 20. |
| 400 (en `detalle.errores`) | `CAMPO_REQUERIDO`, `CAMPO_NO_APLICA` | Captura | Falta un id (municipio, tipo de mercancía) o llegó un campo plano junto con `rutas`. |
| 400 | `RUTA_REQUERIDA` | Margen y conexos | Varias rutas y la petición no dice cuál. `detalle.rutas` = `[{linea_orden, linea_solicitud_id, origen, destino}]`. |
| 400 | `RUTA_INEXISTENTE` | Margen y conexos | La ruta no es de la cotización, o `linea_orden` y `linea_solicitud_id` son de rutas distintas. |
| 400 | `JUSTIFICACION_REQUERIDA` | Margen | Por debajo del sugerido **de esa ruta**; `detalle` trae `margen_sugerido`, `linea_orden` y `linea_solicitud_id`. |
| 409 | `USAR_RESULTADO_NEXO` | `/seguimiento` | `detalle` = `{metodo, endpoint, requiere_if_match}`. |
| 409 | `ESTADO_NO_PERMITE` | Re-solicitar | `datos_carga` en una cotización de varias rutas. |
| 503 | `MUNICIPIOS_NO_DISPONIBLE` | Captura y licitación | El catálogo de municipios no respondió. Nada se guarda. |
| 503 | `CUBICADOR_NO_DISPONIBLE` | `GET /web/tipos-mercancia`; captura (solo con `Cubicador:RespaldoSiFalla = false`) | El cubicador no está configurado o no respondió. |
| 503 / 403 | `NEXO_NO_DISPONIBLE` / `NEXO_SIN_PERMISO` | Captura con conexos | No se pudo leer el costo del conexo en el catálogo de Nexo. |

### 8.3 Crear la solicitud: `POST /web/solicitudes-costeo`

Petición con dos rutas de líneas de negocio distintas:

<!-- esquema: SolicitudCosteoWeb -->
```json
{
  "tipo": "spot",
  "nit_cliente": "900555111",
  "cliente_nombre": "Longyin",
  "linea_negocio": "Importacion",
  "deadline": "2026-10-15T17:00:00-05:00",
  "horas_cargue": 4,
  "horas_descargue": 6.5,
  "notas": "Cita previa en puerto",
  "moneda": "COP",
  "rutas": [
    {
      "origen_municipio_id": 76109,
      "origen": "Buenaventura",
      "destino_municipio_id": 11001,
      "destino": "Bogotá D.C.",
      "tipologia": "Contenedor",
      "clase_viaje": "Contenedor",
      "clase_contenedor": "CNT40.HC",
      "exclusivo": true,
      "consolidado": false,
      "retorna_contenedor": true,
      "ciudad_devolucion_municipio_id": 76109,
      "ciudad_devolucion_contenedor": "Buenaventura",
      "levante_contenedor_diferente": false,
      "peso_kg": 28500.5,
      "valor_mercancia": 250000000,
      "mercancias": [
        { "id_tipo_mercancia": "6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50", "nombre_mercancia": "Estiba de café", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 1425 }
      ],
      "servicios_conexos": [
        { "id_servicio_conexo": "3f2b9c1e-7a4d-4e8b-9c6f-1a2b3c4d5e6f", "cantidad": 2 }
      ]
    },
    {
      "linea_negocio": "Nacional",
      "origen_municipio_id": 76001,
      "origen": "Cali",
      "destino_municipio_id": 76892,
      "destino": "Yumbo",
      "tipologia": "CargaSeca",
      "clase_viaje": "Cupo",
      "horas_cargue": 2,
      "peso_kg": 9000,
      "valor_mercancia": 180000000,
      "mercancias": [
        { "id_tipo_mercancia": "6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50", "nombre_mercancia": "Estiba de cajas", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 450 }
      ]
    }
  ]
}
```

Respuesta 201 (`CotizacionResumen`). Nexo recibió la solicitud y falta el flete; con rutas de líneas distintas, `linea_negocio`
es `Varias`:

<!-- respuesta: POST /web/solicitudes-costeo 201 -->
```json
{
  "id": "5b0f8a3e-2c1d-4e6f-9a7b-3c2d1e0f9a8b",
  "folio": "PCN-900555111-DG-09102026-001",
  "version": 1,
  "nit_cliente": "900555111",
  "origen_cliente": "cliente",
  "origen_margen": null,
  "cliente": "LONGYIN S.A.S.",
  "linea_negocio": "Varias",
  "precio_final": null,
  "margen_aplicado": null,
  "moneda": "COP",
  "trm_aplicada": 0,
  "estado_costeo": "pendiente_costeo",
  "motivo_estado_costeo": null,
  "estado_envio": "sin_enviar",
  "ganada": null,
  "motivo_rechazo": null,
  "pdf_url": null,
  "created_at": "2026-10-09T15:20:11.532+00:00"
}
```

Error 400 con todos los errores juntos (nada queda guardado):

<!-- respuesta: POST /web/solicitudes-costeo 400 -->
```json
{
  "error": {
    "codigo": "SOLICITUD_COSTEO_INVALIDA",
    "mensaje": "Ruta 1: el municipio 'Cartago' es ambiguo: Cartago, Valle del Cauca (id 76147); Cartago, Cartago (Costa Rica) (id 99001).",
    "detalle": {
      "motivo": "MUNICIPIO_AMBIGUO",
      "errores": [
        { "ruta": 0, "campo": "destino", "codigo": "MUNICIPIO_AMBIGUO", "mensaje": "Ruta 1: el municipio 'Cartago' es ambiguo: Cartago, Valle del Cauca (id 76147); Cartago, Cartago (Costa Rica) (id 99001)." },
        { "ruta": 1, "campo": "mercancias[0].id_tipo_mercancia", "codigo": "TIPO_MERCANCIA_INEXISTENTE", "mensaje": "Ruta 2: el tipo de mercancía no está en el catálogo del cubicador." }
      ]
    }
  }
}
```

### 8.4 Catálogo de mercancías: `GET /web/tipos-mercancia`

<!-- respuesta: GET /web/tipos-mercancia 200 -->
```json
[
  { "id": "6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50", "nombre": "Estiba de café" },
  { "id": "9a7b3c2d-1e0f-4a8b-8c5d-2e3f4a5b6c7d", "nombre": "Tambor de químicos" }
]
```

### 8.5 Licitación: `POST /web/licitaciones`

Cada fila es una ruta, sin vehículo y con mercancías y conexos. `valor_estimado` y `referencia` ya no van:

<!-- esquema: LicitacionWeb -->
```json
{
  "nit_cliente": "900555111",
  "cliente_nombre": "Agencia JF",
  "linea_negocio": "Nacional",
  "deadline": "2026-10-20T12:00:00-05:00",
  "notas": "Pliego de octubre",
  "hoja": "Tarifas",
  "moneda": "COP",
  "lineas": [
    {
      "fila": 0,
      "origen": "Cali",
      "origen_municipio_id": 76001,
      "destino": "Bogotá D.C.",
      "destino_municipio_id": 11001,
      "tipologia": "CargaSeca",
      "clase_viaje": "Cupo",
      "peso_kg": 12000,
      "mercancias": [
        { "id_tipo_mercancia": "6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50", "nombre_mercancia": "Estiba de cajas", "cantidad": 24, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 500 }
      ]
    }
  ]
}
```

Respuesta 201 (`Licitacion`, sin `referencia`). `tipo_vehiculo` de cada línea es el de sus camiones (lo pone el cubicador o
la Mesa), `null` mientras no lo haya:

<!-- respuesta: POST /web/licitaciones 201 -->
```json
{
  "folio": "PCN-900555111-DG-09102026-002",
  "cliente_nombre": "AGENCIA JF S.A.S.",
  "nit_cliente": "900555111",
  "deadline": "2026-10-20T17:00:00+00:00",
  "estado_costeo": "pendiente_costeo",
  "motivo_estado_costeo": null,
  "moneda": "COP",
  "trm_aplicada": 0,
  "precio_final": null,
  "lineas_costeadas": 0,
  "lineas_pendientes": 1,
  "archivo_nombre": null,
  "hoja": "Tarifas",
  "lineas": [
    { "orden": 1, "fila": 0, "origen": "Cali", "destino": "Bogotá D.C.", "tipo_vehiculo": null, "peso_kg": 12000, "estado_costeo": "pendiente_costeo", "precio_final": null }
  ]
}
```

### 8.6 Costeo: `GET /web/cotizaciones/{folio}/costeo`

Dos rutas con precio. `precio_final` = 5.294.118 + 3.529.412 = 8.823.530 (ya incluye los conexos). Los campos de margen de
primer nivel son los de la ruta 1:

<!-- respuesta: GET /web/cotizaciones/{folio}/costeo 200 -->
```json
{
  "folio": "PCN-900555111-DG-09102026-001",
  "estado_costeo": "autocosteo",
  "motivo_estado_costeo": null,
  "costeo_listo_at": "2026-10-09T15:24:40.118+00:00",
  "precio_final": 8823530,
  "margen_aplicado": 0.15,
  "margen_sugerido": 0.15,
  "origen_margen": "politica_linea",
  "moneda": "COP",
  "trm_aplicada": 0,
  "servicios_conexos": [
    { "id": "3f2b9c1e-7a4d-4e8b-9c6f-1a2b3c4d5e6f", "nombre": "Cargue con montacargas", "categoria": "logistico", "cantidad": 2, "precio": 470588, "incluido": true, "linea_orden": 1, "estado_nexo": "incierto" }
  ],
  "pdf_url": null,
  "textos": null,
  "pdf_desactualizado": false,
  "row_version": "7",
  "rutas": [
    {
      "linea_orden": 1,
      "linea_solicitud_id": "0c9e7f3a-5b2d-4c1e-8f6a-9b8c7d6e5f4a",
      "linea_negocio_id": 3,
      "origen": "Buenaventura",
      "destino": "Bogotá D.C.",
      "estado_costeo": "autocosteo",
      "peso_kg": 28500.5,
      "precio_final_linea": 5294118,
      "margen_aplicado": 0.15,
      "margen_sugerido": 0.15,
      "origen_margen": "politica_linea",
      "vehiculos": "1 × 3S3 - PORTACONTENEDORES (28500.5 KG)",
      "camiones": [
        { "tipo_vehiculo": "3S3", "carroceria": "PORTACONTENEDORES", "peso_kg": 28500.5, "estado_costeo": "autocosteo" }
      ],
      "mercancias": [
        { "orden": 1, "id_tipo_mercancia": "6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50", "nombre_mercancia": "Estiba de café", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 1425 }
      ],
      "servicios_conexos": [
        { "id": "3f2b9c1e-7a4d-4e8b-9c6f-1a2b3c4d5e6f", "nombre": "Cargue con montacargas", "categoria": "logistico", "cantidad": 2, "precio": 470588, "incluido": true, "linea_orden": 1, "estado_nexo": "incierto" }
      ],
      "aviso_conexos_nexo": "No se pudo confirmar en Nexo el envío del servicio conexo «Cargue con montacargas»: revísalo en la cotización de Nexo."
    },
    {
      "linea_orden": 2,
      "linea_solicitud_id": "7d6c5b4a-3e2f-4a1b-9c8d-0e1f2a3b4c5d",
      "linea_negocio_id": 1,
      "origen": "Cali",
      "destino": "Yumbo",
      "estado_costeo": "autocosteo",
      "peso_kg": 9000,
      "precio_final_linea": 3529412,
      "margen_aplicado": 0.15,
      "margen_sugerido": 0.18,
      "origen_margen": "negociado",
      "vehiculos": "Vehículo por definir",
      "camiones": [
        { "tipo_vehiculo": null, "carroceria": null, "peso_kg": 9000, "estado_costeo": "autocosteo" }
      ],
      "mercancias": [
        { "orden": 1, "id_tipo_mercancia": "6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50", "nombre_mercancia": "Estiba de cajas", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 450 }
      ],
      "servicios_conexos": [],
      "aviso_conexos_nexo": null
    }
  ]
}
```

`estado_nexo` y `aviso_conexos_nexo` solo vienen con valor en `GET .../costeo`; en las respuestas de las mutaciones son `null`.

### 8.7 Margen por ruta: `PUT /web/cotizaciones/{folio}/costeo/margen`

Encabezado `If-Match: "7"` (el `row_version`). Cuerpo:

<!-- esquema: AjusteMargenWeb -->
```json
{ "linea_orden": 2, "margen": 0.15, "justificacion": "Cliente estratégico: igualamos la tarifa del año pasado." }
```

Responde 200 `CosteoCotizacion` (como 8.6, con el `ETag` nuevo). Sin ruta y con varias rutas:

<!-- respuesta: PUT /web/cotizaciones/{folio}/costeo/margen 400 -->
```json
{
  "error": {
    "codigo": "RUTA_REQUERIDA",
    "mensaje": "La cotización PCN-900555111-DG-09102026-001 tiene 2 rutas: indica a cuál aplica (linea_orden o linea_solicitud_id).",
    "detalle": {
      "rutas": [
        { "linea_orden": 1, "linea_solicitud_id": "0c9e7f3a-5b2d-4c1e-8f6a-9b8c7d6e5f4a", "origen": "Buenaventura", "destino": "Bogotá D.C." },
        { "linea_orden": 2, "linea_solicitud_id": "7d6c5b4a-3e2f-4a1b-9c8d-0e1f2a3b4c5d", "origen": "Cali", "destino": "Yumbo" }
      ]
    }
  }
}
```

### 8.8 Conexos por ruta: `PUT` y `POST /web/cotizaciones/{folio}/costeo/servicios-conexos`

`PUT` (con `If-Match`): lo que **queda** en la ruta. Quitar un conexo borra su fila; cambiar la cantidad conserva el costo
unitario de Nexo.

<!-- esquema: SeleccionServiciosConexos -->
```json
{ "linea_orden": 1, "conexos": [ { "id": "3f2b9c1e-7a4d-4e8b-9c6f-1a2b3c4d5e6f", "cantidad": 1 } ] }
```

`POST` (con `If-Match` e `Idempotency-Key`): agregar conexos del catálogo a una ruta. Todos los items son de la misma ruta.

<!-- esquema: AgregarServiciosConexosWeb -->
```json
{ "items": [ { "linea_orden": 2, "servicio_id": "8b1f3c2a-0d4e-4f5a-9b6c-7d8e9f0a1b2c", "cantidad": 1 } ] }
```

Los dos responden 200 `CosteoCotizacion`, o 400 `RUTA_REQUERIDA`/`RUTA_INEXISTENTE` como en 8.7.

### 8.9 Resultado: `POST /web/cotizaciones/{folio}/resultado`

Encabezado `If-Match: "7"` (obligatorio en la perdida sin costeo; opcional en el resto). Cuerpo:

<!-- esquema: ResultadoWeb -->
```json
{ "resultado": "perdida", "motivo": "Precio por encima del presupuesto del cliente", "comentario": "El cliente cerró con otro proveedor." }
```

Responde 200 `CotizacionResumen` con el `ETag` nuevo; una ganada queda con `motivo_rechazo: "No aplica"`.

`/seguimiento` de una cotización que pasó por Nexo:

<!-- respuesta: POST /web/cotizaciones/{folio}/seguimiento 409 -->
```json
{
  "error": {
    "codigo": "USAR_RESULTADO_NEXO",
    "mensaje": "La cotización PCN-900555111-DG-09102026-001 está en Nexo: marca el resultado con POST /web/cotizaciones/PCN-900555111-DG-09102026-001/resultado (con If-Match).",
    "detalle": {
      "metodo": "POST",
      "endpoint": "/web/cotizaciones/PCN-900555111-DG-09102026-001/resultado",
      "requiere_if_match": true
    }
  }
}
```

### 8.10 "Mis cotizaciones" y la nueva versión

`GET /web/mis-cotizaciones`: la línea de negocio, el margen y su origen son los de la ruta 1 (o `Varias` si las rutas tienen
líneas distintas), y `precio_final` es la suma de las rutas, con conexos:

<!-- respuesta: GET /web/mis-cotizaciones 200 -->
```json
{
  "total": 1,
  "cotizaciones": [
    {
      "id": "5b0f8a3e-2c1d-4e6f-9a7b-3c2d1e0f9a8b",
      "folio": "PCN-900555111-DG-09102026-001",
      "version": 1,
      "nit_cliente": "900555111",
      "origen_cliente": "cliente",
      "origen_margen": "politica_linea",
      "cliente": "LONGYIN S.A.S.",
      "linea_negocio": "Varias",
      "precio_final": 8823530,
      "margen_aplicado": 0.15,
      "moneda": "COP",
      "trm_aplicada": 0,
      "estado_costeo": "entregada",
      "motivo_estado_costeo": null,
      "estado_envio": "enviada",
      "ganada": true,
      "motivo_rechazo": "No aplica",
      "pdf_url": "/pdfs/PCN-900555111-DG-09102026-001.pdf",
      "created_at": "2026-10-09T15:20:11.532+00:00"
    }
  ]
}
```

`GET /web/cotizaciones/{id}/nueva-version`: `rutas[]` trae la línea de negocio y el margen de cada ruta.

<!-- respuesta: GET /web/cotizaciones/{id}/nueva-version 200 -->
```json
{
  "cotizacion_origen_id": "5b0f8a3e-2c1d-4e6f-9a7b-3c2d1e0f9a8b",
  "folio_base": "PCN-900555111-DG-09102026-001",
  "version_siguiente": 2,
  "cliente_nombre": "LONGYIN S.A.S.",
  "nit_cliente": "900555111",
  "origen_cliente": "cliente",
  "linea_negocio": "Varias",
  "costo": null,
  "margen_politica": 0.15,
  "margen_aplicado": 0.15,
  "origen_margen": "politica_linea",
  "metodo_calculo": "margen",
  "moneda": "COP",
  "rutas": [
    { "linea_orden": 1, "linea_negocio_id": 3, "linea_negocio": "Importacion", "origen": "Buenaventura", "destino": "Bogotá D.C.", "margen_politica": 0.15, "margen_aplicado": 0.15, "origen_margen": "politica_linea", "metodo_calculo": "margen" },
    { "linea_orden": 2, "linea_negocio_id": 1, "linea_negocio": "Nacional", "origen": "Cali", "destino": "Yumbo", "margen_politica": 0.18, "margen_aplicado": 0.15, "origen_margen": "negociado", "metodo_calculo": "margen" }
  ]
}
```

### 8.11 Compatibilidad que se mantiene

| Qué | Hasta cuándo |
|---|---|
| Formato plano de `POST /web/solicitudes-costeo` (una ruta en la raíz, `deprecated`) | Cuando salga HU-021-FE |
| `conexos_descartados` en la respuesta de re-solicitar (siempre `[]`, `deprecated`) | Cuando salga HU-021-FE |
| `tipo_vehiculo` y `carroceria` en la ruta (`deprecated`; se ignoran salvo `Captura:AceptarVehiculoCliente`) | Cuando exista el cubicador (C1) |
| `incluidos` en `PUT .../costeo/servicios-conexos` (compatibilidad de HU-005.7) | Sin fecha |
| `valor_estimado` y `referencia` en la petición: ya no están en el contrato, pero si llegan se ignoran sin error | Sin fecha |
