# Contrato de la API web de Cotifly (HU-004.1)

Contrato congelado que consume el módulo Angular de ClientePro (HU-004, `clientepro-frontend`) y que implementa el backend .NET 10 (HU-003). La fuente de verdad es [`backend-dotnet/specs/openapi.yaml`](../../backend-dotnet/specs/openapi.yaml); este documento resume lo que el frontend necesita saber para integrarse sin leer el código.

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-19

- Fecha de congelamiento: 2026-09-24
- Issue: #17 (HU-004.1)
- Validación: `redocly lint` sin errores

## 1. Origen del contrato

El contrato se congeló el 2026-09-24 a partir de la API web del bot anterior, que se retiró del repositorio en HU-022.1
(su último estado está en el tag `python-final`), y lo implementa el backend .NET 10:

- `mis-cotizaciones`, `enviar` y `seguimiento` conservan la forma de la última versión funcional del bot anterior, con los
  campos renombrados en HU-002.2 (`efectiva`/`nota` → `ganada`/`motivo_rechazo`; `comercial_email` → identidad por token).
- `/web/login` se reemplazó por `POST /api/auth/login-clientepro` (patrón "Login SSO — Cliente Pro").
- La creación de cotizaciones es solo por formulario (`POST /web/solicitudes-costeo` y `POST /web/licitaciones`, ADR-018);
  Google Chat se retiró en #105.
- Los demás endpoints se agregaron después (HU-003.5, HU-003.6, HU-004.9, HU-010.1, HU-013, HU-021 y HU-023) y están en
  `specs/openapi.yaml`.

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
| `GET /web/catalogos-flete` | ambos | Tipos de vehículo, carrocerías, clases de viaje y clases de contenedor del formulario (catálogos maestros de Nexo; `clases_contenedor` desde HU-027.1, puede venir vacía). Ver §3.2 |
| `GET /web/municipios` | ambos | Buscador de origen y destino (`?q=&departamento=`, máx. 50) o catálogo completo sin filtros. 503 `MUNICIPIOS_NO_DISPONIBLE` si Nexo no responde (antes 400). Ver §8.1, fila 5 |
| `GET /web/mis-cotizaciones` | ambos | Cotizaciones propias (máx. 100) |
| `GET /web/cotizaciones/regional` | gerente_comercial | HU-016.1 (#154): cotizaciones de los comerciales de su regional (máx. 100), con la forma de `mis-cotizaciones` y el comercial dueño en cada fila. Ver §8.10.1 |
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
| `GET /web/metricas/regional` | gerente_comercial | Tablero del gerente (HU-010.3): su regional, filtrable por `usuario_id` y por `anio`/`mes` (HU-010.5), con lista de comerciales, serie mensual y concentración por ciudad. Ver §3.1 |
| `GET /web/metricas/actividad` y `GET /web/metricas/ajustes-margen` | — | **Retirados (HU-014.3, #152):** responden 404. Eran el borrador del tablero; usar `/web/metricas/comercial` y `/regional` |
| `GET /pdfs/{archivo}` | dueño o gerente de su regional | Descarga del PDF. Exige `Authorization`, así que se baja con HttpClient como blob, no con un `<a href>`. El enlace firmado `?t=` (ADR-011) se retiró en #105 junto con Google Chat |

Todos los campos de `/web/*` van en snake_case, incluidos los de los endpoints nuevos, en la respuesta **y en el cuerpo de la petición** (`motivo_rechazo`, `metodo_calculo`, `is_active`; un `motivoRechazo` se ignora). Los campos `nullable` siempre vienen, con `null` si no tienen valor. Un número enviado como texto (`"margen": "0.2"`) es un cuerpo inválido (400).

"Ambos" = `comercial` o `gerente_comercial`; cada uno ve solo sus cotizaciones. El gerente puede descargar los PDF de los comerciales de su regional; si su regional aún no se conoce, solo los suyos (fail-closed).

**Folio** (ADR-011, aprobado el 2026-09-25, con iniciales desde el mismo día): lo genera siempre el servidor, `PCN-[{nitCliente}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]`: NIT solo con dígitos cuando la cotización lo tiene, dos letras con las iniciales del comercial (primer nombre + primer apellido, sin tildes; p. ej. "Duban Guerra Castro" → `DG`), fecha de Bogotá, `NNN` consecutivo diario de esas iniciales (`001`…`999`; dos comerciales con las mismas iniciales comparten la secuencia) y `-vN` solo en versiones nuevas (sobre el folio de la v1). Ejemplos: `PCN-DG-25092026-001`, `PCN-9000472281-DG-25092026-002`, `PCN-9000472281-DG-25092026-002-v2`. Máximo 50 caracteres. El frontend no debe asumir el formato para nada distinto de mostrarlo.

### 3.1 Métricas del tablero (HU-010.1, #45)

Decisiones completas en `backend-dotnet/specs/decisiones/ADR-016-metricas-agregadas-y-regional.md`; esquemas y ejemplos en el tag `metricas` del spec.

**Parámetros.** `desde` y `hasta` son opcionales (`AAAA-MM-DD`, fechas de Bogotá, ambas incluidas). Por defecto van del 1 de enero del año en curso a hoy; máximo 3 años. En `/regional`, `usuario_id` es opcional (UUID de un comercial de la regional). Fechas mal formadas, `hasta < desde` o un `usuario_id` que no es UUID: 400.
**Año y mes (HU-010.5, #187), solo en `/regional`:** `?anio=2026` es el año completo y `?anio=2026&mes=10` ese mes completo (fechas de Bogotá). Si llega `anio`, reemplaza a `desde`/`hasta` (se ignoran). `mes` sin `anio`, `mes` fuera de 1 a 12 o `anio` fuera de 2020 al año en curso más uno (o no enteros): 400. Sin `anio` ni `mes`, nada cambia; `/comercial` no los acepta.

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
  nunca la `etiqueta`; HU-014 #188: `tipo_vehiculo` y `carroceria` deben ser códigos que **Nexo** conozca, ya no basta el catálogo
  del cubicador, y se aceptan con otras mayúsculas, sin tildes o con `_`/`-` por espacio: `FURGON_REFRIGERADO` = `FURGON REFRIGERADO`),
  `tipologia` (enum de 7 valores) y `clase_contenedor` (el `valor` de `clases_contenedor` de `GET /web/catalogos-flete`: `CNT20.IT`,
  `CNT20.RE`, `CNT20.ST`, `CNT40.HC`, `CNT40.RE`; más la lista provisional `CNT20.FT`, `CNT20.OP`, `CNT40.FT`, `CNT40.OP`, que no viene
  en el catálogo). Otro texto es 400
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
- **`clases_contenedor` (HU-027.1, #179):** las clases de contenedor de Nexo (`GET /api/catalogos/clases-contenedor`). En Nexo la
  `descripcion` es el código, así que `valor` = `etiqueta` = `CNT40.HC`, el mismo texto que valida la captura. No trae la lista
  provisional. Puede venir `[]` con 200 (Nexo sin base de SAP configurada o el catálogo caído): deshabilitar el selector y ofrecer
  reintento.

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
`docs/docs_front/GUIA_FRONTEND_COTIFLY.md` (secciones 5.3 y 10, "Asistente por folio").

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
| `{"error": "texto"}` (`ErrorWeb`) | Autenticación (401), permisos de rol (403), recurso inexistente o ajeno (404), cuerpo demasiado grande (413), tipo de contenido (415), límite de peticiones (429) y el 400 de validación del cuerpo (`Cuerpo inválido...`, correo, teléfono, seguimiento, `Idempotency-Key` fuera de largo). En Python este formato solo lo usaban el 401, el 400 de `sesion_id` y el 404 de cotización; ver §7 para los que cambian. |
| `{"error": {"codigo", "mensaje", "detalle?"}}` (`ErrorNegocio`) | **El resto de errores de negocio de `/web/*`, que son la mayoría**: los 400 con código (`SOLICITUD_COSTEO_INVALIDA`, `RUTA_REQUERIDA`, los del cubicador...), los 409 de estado y de concurrencia, el 428, el 403 `NEXO_SIN_PERMISO` y los 503 de dependencias. Por ejemplo, `/enviar` con `CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO` o `CORREO_ENVIO_FALLIDO`. La lista de códigos está en `ErrorNegocio` del spec. Un mismo estado HTTP puede traer cualquiera de las dos formas: distinguir por el tipo de `error` (texto u objeto). En `/web/chat` los errores de las herramientas no son un HTTP de error: llegan en `errores` de la respuesta 200 (ej. `TRM_NO_DISPONIBLE`, ninguna fuente de la TRM respondió). |

| Código | Significado para el frontend |
|---|---|
| 401 | Sesión vencida o revocada: intentar refresh una vez. |
| 403 | El rol no permite la operación (por ejemplo, un comercial en `/web/admin/*`). |
| 404 | No existe o no es del usuario. Son indistinguibles a propósito (mismo cuerpo `{"error":"Cotización no encontrada o no es tuya."}`, también en `/pdfs`). |
| 413 | El cuerpo de una petición a `/web/*` supera 64 KB. |
| 429 | Demasiadas peticiones: reintentar después de unos segundos. |
| 409 | `ErrorNegocio`: conflictos de estado del costeo (`ESTADO_NO_PERMITE`, `COSTEO_NO_LISTO`, `PDF_YA_GENERADO`, `PDF_YA_ENVIADO`...), `IDEMPOTENCY_KEY_REUTILIZADA` (D4, 2026-10-16: la misma `Idempotency-Key` con otro cuerpo en la captura, los conexos o re-solicitar; antes era 400 `{"error": "texto"}`; generar una clave nueva) y `CONFLICTO_CONCURRENCIA` (el `If-Match` no coincide: recargar y repetir). `GET /web/metricas/regional` y `GET /web/cotizaciones/regional` de un gerente sin regional: `REGIONAL_NO_ASIGNADA`. |
| 428 | `ErrorNegocio` `PRECONDICION_REQUERIDA`: falta `If-Match` en una mutación de la etapa final. **No hay 412**: el conflicto de versión es el 409 `CONFLICTO_CONCURRENCIA`. |
| 503 | `POST /enviar` con el correo deshabilitado en el servidor: `ErrorNegocio` con `CORREO_DESHABILITADO`. `GET /web/metricas/regional` y `GET /web/cotizaciones/regional` con la fuente de la regional caída: `REGIONAL_NO_DISPONIBLE`. `GET /web/catalogos-flete` y `GET /web/municipios` con Nexo sin responder: `CATALOGOS_FLETE_NO_DISPONIBLE` y `MUNICIPIOS_NO_DISPONIBLE`. |

## 6. CORS

El backend Python no tenía CORS: `chat.html` funcionaba porque se servía desde el mismo origen. **No se agrega CORS al `orquestador` Python**, porque el módulo Angular va a consumir solo el backend .NET (corte único, HU-003.1). En .NET, los orígenes permitidos se configuran con `Cors__OrigenesPermitidos__0`, `__1`, etc. (origen exacto, `https://host[:puerto]`, sin ruta ni comodines; en producción la API no arranca sin al menos uno). Se permiten los métodos `GET`, `POST` y `PUT` (ningún endpoint usa `DELETE` ni `PATCH`) y, como lista explícita, los headers `Authorization`, `Content-Type`, `If-Match` (concurrencia optimista) e `Idempotency-Key` (captura, conexos y re-solicitar), sin cookies ni credenciales; el preflight se cachea 10 minutos. La respuesta expone (`Access-Control-Expose-Headers`) `ETag`, `Location`, `Retry-After` y `Content-Disposition`, para que el frontend lea la versión de la cotización, la URL de la versión `-vN`, la espera de un 429 o un 503 `SERVICIO_OCUPADO` y el nombre de un archivo descargado. Cualquier otro header propio (`X-Request-Id`, `X-Correlation-Id`...) hace fallar el preflight: la API no tiene id de correlación.

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
| `num_cotizaciones_enviadas` cuenta `enviada` y `reenviada` | La vista del 0001 contaba solo `enviada`; la corrigió el script `0003`. La vista y su endpoint (`/web/metricas/actividad`) se retiraron en HU-014.3 (#152) |

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
| 3 | **`tipo_vehiculo` y `carroceria` se ignoran** (decisión 7: los define el cubicador o la Mesa de Costeo de Nexo). | Quitarlos del formulario y del mapeo del Excel. El vehículo se lee de `rutas[].camiones[]`. Excepción: con la bandera del servidor `Captura:AceptarVehiculoCliente = true` (encendida por defecto desde #188) se usan en el camión del respaldo mientras no haya cubicador (HU-021-FE.2). |
| 4 | **`valor_estimado` y `referencia` salen del contrato** (decisión 1), también `referencia` de la vista `Licitacion`. | Dejar de enviarlos y de leerlos. Si se envían, se ignoran sin error (queda un aviso en el log). |
| 5 | **Municipios con id obligatorio.** Origen, destino y las ciudades de devolución y de levante van con su id de `GET /web/municipios` y una copia del nombre. Toda captura consulta el catálogo: si no responde, **503 `MUNICIPIOS_NO_DISPONIBLE`** y nada se guarda. HU-023 (#176): el buscador usa `GET /web/municipios?q=<texto>&departamento=<depto>`, que pasa a `GET /api/captura-solicitudes/ciudades` de Nexo (máximo 50, solo Colombia); sin filtros sigue siendo el catálogo completo. | Enviar siempre el `*_municipio_id` elegido en la búsqueda. Ante el 503, ofrecer Reintentar. Ante `MUNICIPIO_AMBIGUO`, mostrar los candidatos y repetir con el id elegido. |
| 6 | **Mercancías con `codigo_mercancia` obligatorio** (HU-023.3, #158: el `codigo` del catálogo del cubicador, `GET /web/tipos-mercancia`; obligatorio con el cubicador configurado) y `nombre_mercancia` (antes `descripcion`). `id_tipo_mercancia` es obsoleto: se acepta y se traduce al código. | Usar el desplegable del catálogo y mandar su `codigo`. Sin cubicador, el endpoint responde 503 `CUBICADOR_NO_DISPONIBLE`: el formulario sigue con los tipos que ya conoce. |
| 7 | **Margen y conexos por ruta.** `PUT .../costeo/margen` y `PUT`/`POST .../costeo/servicios-conexos` llevan `linea_orden` (o `linea_solicitud_id`). Con varias rutas y sin ruta: **400 `RUTA_REQUERIDA`** con `detalle.rutas`. | Mostrar un selector de ruta y repetir la petición. Los campos de margen de primer nivel de `CosteoCotizacion` son los de la ruta 1 (compatibilidad). |
| 8 | **`servicios_conexos[].estado_nexo`** (`pendiente`, `enviado`, `incierto`, `fallido_definitivo`) y **`rutas[].aviso_conexos_nexo`**. | Mostrar el aviso cuando venga: el comercial o la Mesa deben revisar ese conexo en Nexo. Nunca reintentar desde el frontend. |
| 9 | **`/resultado` en vez de `/seguimiento`.** Una cotización que pasó por Nexo responde 409 `USAR_RESULTADO_NEXO` en `/seguimiento`, y el `detalle` dice a dónde ir: `metodo`, `endpoint` y `requiere_if_match`. | Llamar a `POST /web/cotizaciones/{folio}/resultado` con `If-Match` (el `row_version` del costeo). Obligatorio en la perdida sin costeo (428 sin él). |
| 10 | **`motivo_rechazo` de una ganada es `"No aplica"`** (HU-021.13). | Mostrar el motivo solo en las perdidas. |
| 11 | **"Mis cotizaciones" y la nueva versión por ruta.** `linea_negocio` es la de la ruta 1 o `Varias`, y `margen_aplicado`/`origen_margen`, los de la ruta 1. `DatosNuevaVersion.rutas[]` trae los de cada ruta. | Mostrar `Varias` tal cual. Para el detalle, usar `GET .../costeo` o `rutas[]`. |
| 12 | **`conexos_descartados`** de la re-solicitud es siempre `[]` (`deprecated`; se retira con HU-021-FE). Los conexos se conservan y se avisan en `conexos_conservados`. | Leer `conexos_conservados`. |
| 13 | **Re-solicitar con `datos_carga`** solo aplica a una cotización de **una** ruta (con varias, 409 `ESTADO_NO_PERMITE` con `detalle.motivo` = `VARIAS_RUTAS` en un formulario o `LICITACION` en una licitación; D9, #139). | Con varias rutas, crear la versión con `nueva_version: true` y sin `datos_carga`. |
| 14 | **`POST /web/cubicacion` ("Calcular cubicaje") cambia de forma** (HU-023.2, #157): ahora pregunta al cubicador y responde una **lista de vehículos** (`vehiculos[]`) con el resultado, el motivo, la condicionada, las advertencias y la extradimensión. Ya no trae `sugerencia`, `sugerencia_flota`, clase de viaje ni clase de contenedor. Cada ítem lleva `codigo_mercancia`; **un ítem sin `codigo_mercancia` ni `descripcion` (antes se cubicaba) ahora es 400 `MERCANCIA_DESCONOCIDA`**, porque el cubicador necesita el tipo de mercancía. Los errores pasan a ser los del cubicador (503 `CUBICADOR_NO_DISPONIBLE` en vez de 400 `CUBICACION_NO_DISPONIBLE`). | Ver §8.12. Pintar la lista de vehículos y quitar el prellenado de clase de viaje y de contenedor. Mandar `codigo_mercancia` del desplegable (mientras tanto sirve `descripcion`). |
| 15 | **`POST /web/cubicacion` puede responder 202** (opción D, 2026-10-19): si el cubicador no termina en 8 s, responde 202 con `{id, estado: "en_proceso", consultar_en}` y el resultado se consulta en `GET /web/cubicacion/{id}`. Nuevo 503 `CUBICADOR_OCUPADO` con `Retry-After`. | Ver §8.12.1: "Calculando…", consultar cada 2-3 s, 404 = volver a calcular. |
| 16 | **`POST /web/cubicacion` valida `codigo_mercancia` contra el catálogo antes de llamar al cubicador** (D-1019-1, E2E 2026-10-19). Un código desconocido o inactivo es 400 `MERCANCIA_DESCONOCIDA` y uno no cubicable 400 `MERCANCIA_NO_CUBICABLE`, ahora **con `detalle.item`** (base 0) y `detalle.campo = "codigo_mercancia"`. Antes llegaba sin `detalle.item`, con el texto del cubicador, y después de ocuparlo. El código se compara sin mayúsculas y viaja como está en el catálogo. Si el catálogo no responde, decide el cubicador como antes. En la captura, el rechazo del cubicador con el catálogo caído trae `detalle.ruta`, `detalle.item` y `detalle.campo` cuando se sabe qué mercancía es. | Marcar el ítem con `detalle.item`, igual que D1. No hace falta otro cambio. |

### 8.2 Códigos de error nuevos de HU-021

| HTTP | Código | Dónde | Detalle |
|---|---|---|---|
| 400 | `SOLICITUD_COSTEO_INVALIDA` | Captura | Ahora con **todos** los errores en `detalle.errores[]` = `{ruta, campo, codigo, mensaje}` (`ruta` base 0, `null` si es de la raíz). |
| 400 (en `detalle.errores`) | `MUNICIPIO_INEXISTENTE`, `MUNICIPIO_AMBIGUO`, `MUNICIPIO_NO_ENCONTRADO` | Captura y licitación | El id no existe; el nombre tiene varios candidatos (el mensaje trae los ids) o ninguno. |
| 400 (en `detalle.errores`) | `MERCANCIA_DESCONOCIDA`, `MERCANCIA_NO_CUBICABLE` | Captura y licitación | HU-023.3: `mercancias[i].codigo_mercancia` no está activo en el catálogo del cubicador, o no se puede cubicar. |
| 400 (en `detalle.errores`) | `TIPO_MERCANCIA_INEXISTENTE` | Captura y licitación | Solo con el campo obsoleto: `mercancias[i].id_tipo_mercancia` (sin `codigo_mercancia`) no está en el catálogo del cubicador. |
| 400 (en `detalle.errores`) | `CONEXO_NO_ENCONTRADO`, `CONEXO_DUPLICADO`, `DEMASIADOS_CONEXOS` | Captura | Conexo fuera del catálogo de Nexo, repetido en la ruta o más de 20. |
| 400 (en `detalle.errores`) | `CAMPO_REQUERIDO`, `CAMPO_NO_APLICA` | Captura | Falta un id de municipio o el `codigo_mercancia`, o llegó un campo plano junto con `rutas`. |
| 400 | `RUTA_REQUERIDA` | Margen y conexos | Varias rutas y la petición no dice cuál. `detalle.rutas` = `[{linea_orden, linea_solicitud_id, origen, destino}]`. |
| 400 | `RUTA_INEXISTENTE` | Margen y conexos | La ruta no es de la cotización, o `linea_orden` y `linea_solicitud_id` son de rutas distintas. |
| 400 | `JUSTIFICACION_REQUERIDA` | Margen | Por debajo del sugerido **de esa ruta**; `detalle` trae `margen_sugerido`, `linea_orden` y `linea_solicitud_id`. |
| 409 | `USAR_RESULTADO_NEXO` | `/seguimiento` | `detalle` = `{metodo, endpoint, requiere_if_match}`. |
| 409 | `ESTADO_NO_PERMITE` | Re-solicitar | `datos_carga` en una cotización de varias rutas: `detalle.motivo` = `VARIAS_RUTAS` (formulario) o `LICITACION` (licitación). |
| 503 | `MUNICIPIOS_NO_DISPONIBLE` | `GET /web/municipios` (antes 400), captura y licitación | El catálogo de municipios no respondió (Nexo caído, tiempo agotado o un 429 de Nexo; sin `Retry-After`, los municipios no comparten el cupo de catálogos). Nada se guarda; ofrecer Reintentar. |
| 503 | `CUBICADOR_NO_DISPONIBLE` | `GET /web/tipos-mercancia`, `/web/tipos-carroceria`, `/web/tipos-vehiculo`; `POST /web/cubicacion` (HU-023.2, sin respaldo); captura (solo con `Cubicador:RespaldoSiFalla = false`) | El cubicador no está configurado o no respondió. |
| 503 | `CUBICADOR_RESPUESTA_INVALIDA` | Captura y licitación | HU-023.1: el cubicador respondió algo fuera del contrato (por ejemplo un camión sin peso). No se guarda nada; reintentar o avisar a soporte. |
| 503 | `CUBICADOR_NO_AUTORIZADO` | Captura y licitación | HU-023.1: el cubicador rechazó la llave de Cotifly (nunca va al respaldo). No es la sesión del comercial: no cerrar sesión. |
| 400 | `MERCANCIAS_REQUERIDAS`, `MERCANCIA_DESCONOCIDA`, `MERCANCIA_NO_CUBICABLE`, `DIMENSIONES_INVALIDAS` | Captura y licitación | HU-023.1: el cubicador rechazó las mercancías de una ruta; `mensaje` trae el detalle del cubicador. No se guarda nada. D-1019-1: cuando se sabe qué mercancía es, `detalle.ruta` y `detalle.item` (base 0) y `detalle.campo`. |
| 400 | `CUBICACION_DEMASIADO_GRANDE` | Captura, licitación, recálculo de vehículos y `POST /web/cubicacion` | **Vigente** desde HU-023 (#176): la ruta o el cálculo pasa de 10.000 unidades (tope del cubicador; se valida antes de llamarlo, con el cubicador configurado). Partir la carga en varias rutas. No se guarda nada. |
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
        { "codigo_mercancia": "CAFE_EN_ESTIBAS", "nombre_mercancia": "Estiba de café", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 1425 }
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
        { "codigo_mercancia": "CAFE_EN_ESTIBAS", "nombre_mercancia": "Estiba de cajas", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 450 }
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
  "created_at": "2026-10-09T15:20:11.532+00:00",
  "deadline": "2026-10-15T22:00:00+00:00",
  "score_prioridad": 62.5
}
```

`deadline` y `score_prioridad` (HU-018.1, #155) vienen en todo `CotizacionResumen` (también en "Mis cotizaciones"): la fecha
límite del formulario, en UTC, y el `scorePrioridad` que Nexo devolvió al crear la solicitud (ver 8.6).

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
        { "ruta": 1, "campo": "mercancias[0].codigo_mercancia", "codigo": "MERCANCIA_DESCONOCIDA", "mensaje": "La mercancía 1 ('CAFE_TOSTADO') no está activa en el catálogo del cubicador (GET /web/tipos-mercancia)." }
      ]
    }
  }
}
```

### 8.4 Catálogos del cubicador: `GET /web/tipos-mercancia`, `/web/tipos-carroceria` y `/web/tipos-vehiculo`

HU-023.1 (#156). El desplegable de mercancías usa `codigo` (lo que viaja al cubicador) y solo ofrece las `activo = true`; puede avisar
de las `condicionada_cubicacion = true` con su `comentario_condicion`. `id` está obsoleto (solo traduce el `id_tipo_mercancia`
anterior).

<!-- respuesta: GET /web/tipos-mercancia 200 -->
```json
[
  { "codigo": "CAFE_EN_ESTIBAS", "nombre": "Estiba de café", "categoria": "GENERAL", "cubicable": true, "condicionada_cubicacion": false, "comentario_condicion": null, "activo": true, "id": "6f1c2a52-8d1e-4f5b-9a63-0c1b2d3e4f50" },
  { "codigo": "QUIMICOS", "nombre": "Tambor de químicos", "categoria": "PELIGROSA", "cubicable": true, "condicionada_cubicacion": true, "comentario_condicion": "Requiere aprobación de AdminTarifas", "activo": true, "id": null }
]
```

**HU-023.3 (#158): las mercancías de la captura y de la licitación se mandan con `codigo_mercancia`** (el `codigo` de este
catálogo), obligatorio cuando el cubicador está configurado; sin cubicador (este catálogo responde 503) se aceptan sin código y la ruta
va con un camión sin vehículo. `id_tipo_mercancia` queda obsoleto: todavía se acepta y se traduce al código, pero el formulario nuevo
debe mandar el código. La captura del formulario es **todo o nada**: si el cubicador rechaza una ruta no se guarda ninguna. La
licitación con cubicador (HU-023.5, #160) se cubica en segundo plano (ver 8.5): se guarda de una vez y es "todo o nada" **hacia Nexo**
(no sale hasta que todas sus rutas quedan cubicadas). Errores por mercancía en `detalle.errores`: `CAMPO_REQUERIDO`, `MERCANCIA_DESCONOCIDA` (inactiva o desconocida) y
`MERCANCIA_NO_CUBICABLE`, en `mercancias[i].codigo_mercancia`.

La respuesta 201 de `POST /web/solicitudes-costeo` trae `cubicacion[]` cuando alguna ruta pasó por el cubicador: por ruta
`{linea_orden, linea_solicitud_id, resultado, motivo_sin_seleccion, condicionada, mercancias_condicionadas, advertencias,
extradimension}`. Mostrar los avisos al crear. Desde HU-023.5 (#160) los avisos también se guardan en la ruta (columna
`avisosCubicacion`): `GET .../costeo` los trae en `rutas[].avisos_cubicacion` cuando la ruta los tiene (si no, el campo se omite:
tratarlo como opcional). `GET .../costeo` trae además en cada ruta `resultado_cubicacion`, `motivo_sin_seleccion` y `condicionada`, y en cada camión `volumen_total_m3`, `porcentaje_llenado_volumen` y
`porcentaje_llenado_peso`. Para recalcular los camiones de una ruta antes de enviarla a Nexo:
`POST /web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos` con `If-Match` (409 `ESTADO_NO_PERMITE` si ya está en Nexo:
usar re-solicitar; también mientras el envío está en curso). Su respuesta trae en la ruta recalculada `avisos_cubicacion` (los avisos de
esa llamada, que quedan guardados en la ruta).

`GET /web/tipos-carroceria` devuelve `[{codigo, nombre, activo}]` y `GET /web/tipos-vehiculo` `[{codigo, nombre, carrocerias, activo}]`:
el nombre del vehículo que se muestra en pantalla y en el PDF sale de este último por el código del camión (Cotifly no lo guarda).
HU-023 (#176): sale del catálogo **canónico** del cubicador `GET /vehiculos-catalogo` (`2L2`, `C2`, `2S2`, `3S3`…), no de
`/vehiculos` (presets del motor 3D). Si el cubicador no responde, 503 `CUBICADOR_NO_DISPONIBLE`: mostrar el código. Los códigos se
comparan sin distinguir mayúsculas.

HU-023 (#176): en la captura, cada mercancía con `codigo_mercancia` debe traer `peso_kg` (por unidad) **mayor que 0** (400
`POSICION_INVALIDA` en `detalle.errores`, con `campo = mercancias[i].peso_kg`: índice base 0 en el campo y la mercancía en base 1 en el
mensaje, `Mercancía 1: ...`; antes el campo era `posiciones[i]` y el mensaje repetía otro índice), y una ruta no puede pasar de 10.000 unidades en total (400 `CUBICACION_DEMASIADO_GRANDE`). Una
ruta `SIN_SELECCION_AUTOMATICA` puede traer el quinto motivo `BUSQUEDA_AGOTADA`; `GET .../costeo` trae en cada ruta
`mensaje_sin_seleccion` (el texto para el comercial: con `BUSQUEDA_AGOTADA` no dice que la carga no cabe) y, en `avisos_cubicacion`,
la `extradimension` con la estructura nueva. Si la base todavía no tiene el script `07`, `motivo_sin_seleccion` llega en `null` y el
motivo real viene en una advertencia con `codigo = "MOTIVO_SIN_SELECCION"` de `avisos_cubicacion`. Esa advertencia solo existe si la ruta
guarda sus avisos (columna `avisosCubicacion`, HU-023.5); `avisos_cubicacion` es opcional en todo `CosteoCotizacion`. Regla para la UI:
con `SIN_SELECCION_AUTOMATICA`, mostrar siempre `mensaje_sin_seleccion` (el servidor ya lo arma con el motivo de la advertencia cuando
`motivo_sin_seleccion` es `null`).

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
        { "codigo_mercancia": "CAFE_EN_ESTIBAS", "nombre_mercancia": "Estiba de cajas", "cantidad": 24, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 500 }
      ]
    }
  ]
}
```

Respuesta 201 (`Licitacion`, sin `referencia`). `tipo_vehiculo` de cada línea es el de sus camiones (lo pone el cubicador o
la Mesa), `null` mientras no lo haya.

**HU-023.5 (#160): con el cubicador configurado la licitación se cubica en segundo plano.** El 201 llega sin esperar al cubicador:
cada ruta trae `estado_cubicacion = pendiente` y un camión provisional sin vehículo, `progreso_cubicacion.estado = en_curso` y
`estado_costeo = pendiente_envio` (todavía no está en Nexo). El frontend consulta `GET /web/licitaciones/{folio}` cada 3 a 5 s mientras
`progreso_cubicacion.estado = en_curso` y muestra el avance (`cubicadas` de `total`). Al quedar todas cubicadas, la licitación sale
sola a Nexo (`estado_costeo` pasa a `pendiente_costeo`) y cada ruta trae sus `camiones` (código de vehículo y carrocería, peso,
volumen y llenado) y su `resultado_cubicacion`; los avisos del cubicador van en `cubicacion[]` (la misma forma de
`POST /web/solicitudes-costeo`, ahora se guardan). Si una ruta falla (`estado_cubicacion = fallida`, con `error_cubicacion`), el estado
es `con_fallas` y la licitación **no sale a Nexo** hasta `POST /web/licitaciones/{folio}/cubicacion/reintentar` (cuerpo opcional
`{"usar_respaldo": true}` para aceptar con un camión sin vehículo, que resuelve la Mesa de Costeo, **solo las rutas que fallaron porque
el cubicador no respondió** (`error_cubicacion` empieza por `CUBICADOR_NO_DISPONIBLE`); sin él, vuelven a la cola). 409
`ESTADO_NO_PERMITE` si no hay rutas fallidas, o con `usar_respaldo` si las fallidas son rechazos de la mercancía o las medidas
(`MERCANCIA_DESCONOCIDA`, `DIMENSIONES_INVALIDAS`...): esos se corrigen en una versión nueva. Mientras la cubicación no termina, `POST .../costeo/reintentar` y el
recálculo de una ruta responden 409. Sin cubicador, como antes (sin `progreso_cubicacion`):

<!-- respuesta: POST /web/licitaciones 201 -->
```json
{
  "folio": "PCN-900555111-DG-09102026-002",
  "cliente_nombre": "AGENCIA JF S.A.S.",
  "nit_cliente": "900555111",
  "deadline": "2026-10-20T17:00:00+00:00",
  "estado_costeo": "pendiente_envio",
  "motivo_estado_costeo": null,
  "moneda": "COP",
  "trm_aplicada": 0,
  "precio_final": null,
  "lineas_costeadas": 0,
  "lineas_pendientes": 1,
  "archivo_nombre": null,
  "hoja": "Tarifas",
  "lineas": [
    {
      "orden": 1, "fila": 0, "origen": "Cali", "destino": "Bogotá D.C.", "tipo_vehiculo": null, "peso_kg": 12000,
      "estado_costeo": "pendiente_envio", "precio_final": null,
      "estado_cubicacion": "pendiente", "error_cubicacion": null, "resultado_cubicacion": null, "motivo_sin_seleccion": null,
      "condicionada": false,
      "camiones": [
        { "orden": 1, "tipo_vehiculo": null, "carroceria": null, "peso_kg": 12000, "volumen_total_m3": null, "porcentaje_llenado_volumen": null, "porcentaje_llenado_peso": null }
      ]
    }
  ],
  "progreso_cubicacion": { "estado": "en_curso", "total": 1, "cubicadas": 0, "pendientes": 1, "fallidas": 0 },
  "cubicacion": null
}
```

**Código de mercancía sugerido (HU-023.5).** `POST /web/licitaciones/analizar-excel` trae en cada hoja `columna_mercancia`,
`columna_codigo_mercancia` y `mercancias[]`: por fila (`fila`, índice en `filas`), `{texto, codigo_mercancia, nombre_mercancia,
sugerido, origen, similitud}`. Con la columna de código del Excel se usa tal cual (`origen = excel`, `sugerido = false`); si no, el
tipo más parecido del catálogo del cubicador, sin tildes ni mayúsculas (`origen = parecido`, `sugerido = true`): **mostrarlo como
sugerido y que el comercial lo confirme o lo cambie** antes de mandarlo en `mercancias[].codigo_mercancia`. Sin nada parecido,
`codigo_mercancia = null`. `catalogo_mercancias_disponible = false` cuando no hay cubicador o su catálogo no respondió.

**Excel (`GET /web/licitaciones/{folio}/excel`).** Agrega la columna `Camiones Cotifly` (un camión por línea de la celda) y la hoja
`Camiones Cotifly` (un camión por fila).

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
  "deadline": "2026-10-15T22:00:00+00:00",
  "score_prioridad": 62.5,
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
        { "orden": 1, "codigo_mercancia": "CAFE_EN_ESTIBAS", "nombre_mercancia": "Estiba de café", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 1425 }
      ],
      "servicios_conexos": [
        { "id": "3f2b9c1e-7a4d-4e8b-9c6f-1a2b3c4d5e6f", "nombre": "Cargue con montacargas", "categoria": "logistico", "cantidad": 2, "precio": 470588, "incluido": true, "linea_orden": 1, "estado_nexo": "incierto" }
      ],
      "aviso_conexos_nexo": "No se pudo confirmar en Nexo el envío del servicio conexo «Cargue con montacargas»: revísalo en la cotización de Nexo.",
      "margen_sap_anomalo": true,
      "aviso_margen": "SAP TM propuso un margen de 72.5 % para esta ruta, fuera del rango normal (0 a 60 %): no se aplicó y la ruta usa el margen de la política de su línea de negocio. Revísalo antes de enviar la cotización."
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
        { "orden": 1, "codigo_mercancia": "CAFE_EN_ESTIBAS", "nombre_mercancia": "Estiba de cajas", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 450 }
      ],
      "servicios_conexos": [],
      "aviso_conexos_nexo": null,
      "margen_sap_anomalo": false,
      "aviso_margen": null
    }
  ]
}
```

`estado_nexo` y `aviso_conexos_nexo` solo vienen con valor en `GET .../costeo`; en las respuestas de las mutaciones son `null`.

**El vehículo de cada camión: la Mesa manda (D2, decisión del 2026-10-19).** "Si la Mesa de Costeo realiza un cambio, nos acogemos a
dichos cambios, incluido el tipo de vehículo." `rutas[].camiones[].tipo_vehiculo` y `carroceria` nacen con los códigos del cubicador
(`3S3`, `FURGON`...) o vacíos, y el polling del costeo copia lo que la Mesa deje en Nexo **también en los camiones del cubicador**: si la
Mesa cambia `2S2` por `3S3`, o `FURGON` por `ESTACAS`, el camión pasa a mostrar lo de la Mesa (y el texto de `vehiculos` y el PDF
también). Una diferencia solo de escritura no es un cambio: Nexo devuelve `Furgón` o `FURGON REFRIGERADO` por `FURGON` y
`FURGON_REFRIGERADO`, y se conserva el código del cubicador. Un valor vacío de Nexo no borra el de Cotifly. Puede llegar un nombre de
Nexo en vez del código (`Estacas`): mostrarlo tal cual. Si la Mesa cambia de verdad el vehículo o la carrocería de un camión que ya
tenía, `porcentaje_llenado_volumen` y `porcentaje_llenado_peso` pasan a `null` (el cubicador los calculó para el vehículo anterior y ya
no corresponden; tampoco salen en el Excel de la licitación); `peso_kg` y `volumen_total_m3` se conservan porque son de la carga. Un
cambio solo de escritura o un camión vacío que la Mesa llena no los toca, y si la Mesa vuelve al vehículo original no se recuperan.

`deadline` y `score_prioridad` (HU-018.1, #155) son de la **cabecera** (toda la solicitud), no de cada ruta, y vienen siempre
(también en las respuestas de las mutaciones): `deadline` es la fecha límite del formulario en UTC (`null` si no se dio) y
`score_prioridad` es el `scorePrioridad` que Nexo devolvió al crear la solicitud (`201 CapturaResultDto`), con hasta 4 decimales y
en la escala de Nexo (mostrarlo tal cual). `score_prioridad` es `null` si Nexo no lo mandó, si la solicitud todavía no se envió o si
se envió antes del script 09; es el de la creación y no se actualiza después.

`margen_sap_anomalo` y `aviso_margen` (HU-009, #139): SAP TM propuso para la ruta 1 un margen fuera de [0, 60 %]; no se aplicó, la ruta
usa el de la política de su línea (`origen_margen = politica_linea`, `margen_sugerido` = el de la política) y `aviso_margen` lo explica
al comercial. Sin anomalía, `false` y `null`.

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
      "created_at": "2026-10-09T15:20:11.532+00:00",
      "deadline": "2026-10-15T22:00:00+00:00",
      "score_prioridad": 62.5
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

### 8.10.1 Listado regional del gerente: `GET /web/cotizaciones/regional` (HU-016.1, #154)

Vista "Todas las cotizaciones" del gerente comercial. Solo `gerente_comercial` (un `comercial` recibe 403). La regional es la del
gerente, resuelta en el servidor con la misma regla que `GET /web/metricas/regional`; no se manda por parámetro y nunca salen
cotizaciones de otra regional. El propio gerente es miembro de su regional, así que sus cotizaciones también salen.

- Cada fila es un `CotizacionResumen` (la misma forma de `GET /web/mis-cotizaciones`: línea de la ruta 1 o `Varias`, `precio_final` de
  la cabecera) más `comercial` = `{usuario_id, nombre_usuario, nombre_completo}` del dueño. `comercial` solo viene en este endpoint.
- Máximo 100 en total (no por comercial), de la más reciente a la más antigua (`created_at` y, a igualdad, `folio` descendente). Sin
  filtros ni paginación: para filtrar por comercial, el frontend filtra la lista por `comercial.usuario_id`.
- Errores: 409 `REGIONAL_NO_ASIGNADA` (gerente sin regional; nunca se devuelven todas las regionales) y 503 `REGIONAL_NO_DISPONIBLE`
  (la fuente de la regional está configurada pero caída), como en el tablero del gerente.

<!-- respuesta: GET /web/cotizaciones/regional 200 -->
```json
{
  "regional": "ANTIOQUIA",
  "fuente_regional": "usuarios_web",
  "total": 1,
  "cotizaciones": [
    {
      "id": "5b0f8a3e-2c1d-4e6f-9a7b-3c2d1e0f9a8b",
      "folio": "PCN-900555111-AP-09102026-001",
      "version": 1,
      "nit_cliente": "900555111",
      "origen_cliente": "cliente",
      "origen_margen": "politica_linea",
      "cliente": "LONGYIN S.A.S.",
      "linea_negocio": "Nacional",
      "precio_final": 8823530,
      "margen_aplicado": 0.15,
      "moneda": "COP",
      "trm_aplicada": 0,
      "estado_costeo": "entregada",
      "motivo_estado_costeo": null,
      "estado_envio": "sin_enviar",
      "ganada": null,
      "motivo_rechazo": null,
      "pdf_url": "/pdfs/PCN-900555111-AP-09102026-001.pdf",
      "created_at": "2026-10-09T15:20:11.532+00:00",
      "deadline": "2026-10-15T22:00:00+00:00",
      "score_prioridad": 62.5,
      "comercial": {
        "usuario_id": "5f0c2a8e-4b1d-4c55-9a51-2f7d0c1e9b10",
        "nombre_usuario": "APEREZ",
        "nombre_completo": "Ana Pérez"
      }
    }
  ]
}
```

### 8.11 Compatibilidad que se mantiene

| Qué | Hasta cuándo |
|---|---|
| Formato plano de `POST /web/solicitudes-costeo` (una ruta en la raíz, `deprecated`) | Cuando salga HU-021-FE |
| `conexos_descartados` en la respuesta de re-solicitar (siempre `[]`, `deprecated`) | Cuando salga HU-021-FE |
| `tipo_vehiculo` y `carroceria` en la ruta (`deprecated`; se ignoran salvo `Captura:AceptarVehiculoCliente`; con la bandera, solo códigos que Nexo conozca: HU-014 #188 retiró los del catálogo del cubicador que Nexo no conoce, HU-023.1) | Cuando exista el cubicador (C1) |
| `incluidos` en `PUT .../costeo/servicios-conexos` (compatibilidad de HU-005.7) | Sin fecha |
| `valor_estimado` y `referencia` en la petición: ya no están en el contrato, pero si llegan se ignoran sin error | Sin fecha |
| `datos_carga` en `POST .../costeo/re-solicitar` (`deprecated`, HU-028: usar `PUT .../solicitud`; acepta `codigo_mercancia` y los ids de las ciudades del contenedor) | Cuando el frontend use "Editar solicitud" |
| `GET /web/cotizaciones/{id}/nueva-version` para prellenar (HU-028: usar `GET .../solicitud`, que trae mercancías, conexos y condiciones por ruta) | Cuando el frontend use "Editar solicitud" |

### 8.12 Calcular cubicaje: `POST /web/cubicacion` (HU-023.2, #157)

"Calcular cubicaje" ya no pasa por Nexo (`POST /api/cubicacion` se retira en el corte del cubicador): Cotifly le pregunta al
**cubicador** (`POST /recomendaciones`) con una corrida de prueba `PRUEBA-<guid>` y **no guarda nada**. Es solo una vista previa: los
camiones definitivos se piden al guardar la solicitud (§8.3). **Sin respaldo:** si el cubicador no está configurado o está caído,
responde 503 `CUBICADOR_NO_DISPONIBLE`.

**Petición.** Cada ítem lleva `codigo_mercancia` (el `codigo` de `GET /web/tipos-mercancia`, §8.4). Mientras el formulario no tenga el
desplegable, puede mandar solo `descripcion`: Cotifly la resuelve al tipo **activo** más parecido del catálogo (sin tildes ni
mayúsculas) y lo devuelve en `mercancias[]` con `sugerida = true`. Prefiere no sugerir antes que sugerir mal: si no se parece lo
suficiente, si el tipo tiene números que el texto no trae ("Contenedor 40 pies" nunca es `CONTENEDOR_20`) o si dos tipos quedan casi
empatados, responde 400 `MERCANCIA_DESCONOCIDA` con el número del ítem en el mensaje y en `detalle.item` (base 0). Solo sugiere tipos
cubicables. Un ítem **sin `codigo_mercancia` ni `descripcion`** también es 400 `MERCANCIA_DESCONOCIDA` (antes se cubicaba sin tipo). `clase_viaje` es opcional: viaja al cubicador como preferencia.

**Código contra el catálogo** (D-1019-1, E2E 2026-10-19). Antes de llamar al cubicador, Cotifly busca cada `codigo_mercancia` en el
catálogo de `GET /web/tipos-mercancia` (el mismo, con caché), sin distinguir mayúsculas, y manda el código como está en el catálogo:

- Si el código no está o está inactivo, responde 400 `MERCANCIA_DESCONOCIDA`.
- Si tiene `cubicable: false`, responde 400 `MERCANCIA_NO_CUBICABLE`.
- Los dos traen `detalle.item` (base 0) y `detalle.campo = "codigo_mercancia"`, como D1. El cubicador no se llama.
- Si el catálogo no responde, no se bloquea: el código viaja como llegó y decide el cubicador. Su rechazo trae `detalle.item` y
  `detalle.campo` cuando se sabe qué mercancía es (el cubicador cita el código en su mensaje); si no, viene sin ellos.

<!-- esquema: CubicacionRequest -->
```json
{
  "clase_viaje": "Contenedor",
  "items": [
    { "codigo_mercancia": "CAFE_EN_ESTIBAS", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 1000 },
    { "descripcion": "Bobinas de acero", "cantidad": 2, "largo_cm": 180, "ancho_cm": 180, "alto_cm": 120, "peso_kg": 2250.25 }
  ]
}
```

**Respuesta 200.** Una lista de vehículos (pueden ser de tipos distintos: flota mixta), cada uno con código y nombre del vehículo,
código y nombre de la carrocería, peso que lleva, volumen y los dos porcentajes de llenado. Los nombres salen de los catálogos del
cubicador; si no responden, vienen los códigos. `SIN_SELECCION_AUTOMATICA` trae `motivo_sin_seleccion` (`EXTRADIMENSIONADO`,
`SIN_CANDIDATOS_DIMENSIONALES`, `SUPERA_K_MAX`, `SIN_CARROCERIA_COMPATIBLE` o, desde HU-023 #176, `BUSQUEDA_AGOTADA`), el texto para el
comercial en `mensaje_sin_seleccion` y `vehiculos` vacía. **`BUSQUEDA_AGOTADA` es un límite del algoritmo del cubicador, no de la carga:
no decirle al comercial que su carga no cabe** (usar `mensaje_sin_seleccion`). `condicionada = true` con `mercancias_condicionadas` avisa
que la ruta iría a la Mesa de Costeo; cada mercancía trae el `comentario` de `GET /web/tipos-mercancia` (`comentario_condicion`).

HU-023 (#176), contrato vigente del cubicador: cada vehículo trae además `numero_vehiculo`, `alerta_bajo_llenado`, `heterogenea` y
`mercancias[]` (una fila por posición de `items`, con `indice_posicion` base 0 —puede venir `null` en corridas viejas—, `codigo_mercancia`,
`cantidad`, `peso_kg` y `volumen_m3`). **Dos filas del mismo camión pueden compartir `codigo_mercancia`: si se agrupan por código, se
suman.** `extradimension` cambia de forma: `{hay_extradimension, medidas_estandar: {largo_max_cm, ancho_max_cm, alto_max_cm, peso_max_kg},
piezas: [{indice_posicion, id_tipo_mercancia, codigo_mercancia, excesos[]}]}`. Cada ítem debe traer `peso_kg` mayor que 0 y
`largo_cm`, `ancho_cm` y `alto_cm` entre 0,1 y 999.999 (400 `DIMENSIONES_INVALIDAS` con `detalle.item`, el índice base 0 del ítem, y
`detalle.campo`), y una `cantidad` entre 1 y 100.000 (400 `POSICION_INVALIDA`, con `detalle.item` y `detalle.campo = "cantidad"`): las reglas
de la captura, validadas antes de llamar al cubicador (E2E 2026-10-16, D1) y la suma de `cantidad` no puede pasar de **10.000** (400 `CUBICACION_DEMASIADO_GRANDE`: partir la carga).

<!-- respuesta: POST /web/cubicacion 200 -->
```json
{
  "resultado": "ASIGNADO",
  "motivo_sin_seleccion": null,
  "mensaje_sin_seleccion": null,
  "condicionada": false,
  "vehiculos": [
    { "codigo_vehiculo": "3S3", "nombre_vehiculo": "Tractocamión 3S3", "codigo_carroceria": "FURGON", "nombre_carroceria": "Furgón", "peso_total_kg": 16000, "volumen_total_m3": 62.5, "porcentaje_llenado_volumen": 81.25, "porcentaje_llenado_peso": 47.06,
      "numero_vehiculo": 1, "alerta_bajo_llenado": false, "heterogenea": false,
      "mercancias": [
        { "indice_posicion": 0, "id_tipo_mercancia": "2B21A9C4-7E55-4D7B-9A10-3C1F0E2D4B6A", "codigo_mercancia": "CAFE_EN_ESTIBAS", "cantidad": 15, "peso_kg": 15000, "volumen_m3": 27 }
      ] },
    { "codigo_vehiculo": "C2", "nombre_vehiculo": "Sencillo C2", "codigo_carroceria": "ESTACAS", "nombre_carroceria": "Estacas", "peso_total_kg": 4500.5, "volumen_total_m3": 18.25, "porcentaje_llenado_volumen": 60, "porcentaje_llenado_peso": 75.01,
      "numero_vehiculo": 2, "alerta_bajo_llenado": true, "heterogenea": true,
      "mercancias": [
        { "indice_posicion": 0, "id_tipo_mercancia": "2B21A9C4-7E55-4D7B-9A10-3C1F0E2D4B6A", "codigo_mercancia": "CAFE_EN_ESTIBAS", "cantidad": 5, "peso_kg": 5000, "volumen_m3": 9 },
        { "indice_posicion": 1, "id_tipo_mercancia": null, "codigo_mercancia": "BOBINAS_ACERO", "cantidad": 2, "peso_kg": 4500.5, "volumen_m3": 7.78 }
      ] }
  ],
  "mercancias_condicionadas": [],
  "advertencias": [ { "codigo": "REFRIGERADO", "mensaje": "La mercancía refrigerada va en FURGON: confirmar el equipo de frío." } ],
  "extradimension": null,
  "mercancias": [
    { "codigo_mercancia": "CAFE_EN_ESTIBAS", "nombre": null, "descripcion": null, "sugerida": false },
    { "codigo_mercancia": "BOBINAS_ACERO", "nombre": "Bobinas de acero", "descripcion": "Bobinas de acero", "sugerida": true }
  ]
}
```

<!-- respuesta: POST /web/cubicacion 200 -->
```json
{
  "resultado": "SIN_SELECCION_AUTOMATICA",
  "motivo_sin_seleccion": "EXTRADIMENSIONADO",
  "mensaje_sin_seleccion": "Hay piezas que exceden las medidas estándar: la Mesa de Costeo escoge el vehículo.",
  "condicionada": false,
  "vehiculos": [],
  "mercancias_condicionadas": [],
  "advertencias": [ { "codigo": null, "mensaje": "1 pieza(s) exceden las medidas estándar." } ],
  "extradimension": {
    "hay_extradimension": true,
    "medidas_estandar": { "largo_max_cm": 1220, "ancho_max_cm": 260, "alto_max_cm": 300, "peso_max_kg": 34000 },
    "piezas": [ { "indice_posicion": 0, "id_tipo_mercancia": "9F2C4A6E-1D3B-4F5A-8C7E-0B1D2F3A4C5E", "codigo_mercancia": "BOBINAS_ACERO", "excesos": ["alto 320.0 cm > 300.0 cm"] } ]
  },
  "mercancias": [ { "codigo_mercancia": "BOBINAS_ACERO", "nombre": null, "descripcion": null, "sugerida": false } ]
}
```

**Errores.**

<!-- respuesta: POST /web/cubicacion 400 -->
```json
{
  "error": {
    "codigo": "MERCANCIA_DESCONOCIDA",
    "mensaje": "No encontramos en el catálogo del cubicador una única mercancía parecida a «Electrodomésticos» (ítem 2): escoge el tipo de mercancía del catálogo.",
    "detalle": { "item": 1, "descripcion": "Electrodomésticos" }
  }
}
```

<!-- respuesta: POST /web/cubicacion 400 -->
```json
{
  "error": {
    "codigo": "MERCANCIA_DESCONOCIDA",
    "mensaje": "La mercancía del ítem 2 ('NO_EXISTE_XYZ') no está activa en el catálogo del cubicador: escógela del catálogo.",
    "detalle": { "item": 1, "campo": "codigo_mercancia" }
  }
}
```

<!-- respuesta: POST /web/cubicacion 503 -->
```json
{
  "error": {
    "codigo": "CUBICADOR_NO_DISPONIBLE",
    "mensaje": "No se pudo calcular el cubicaje: el cubicador no está disponible en este momento. Intenta de nuevo en unos minutos."
  }
}
```

| HTTP | Código | Cuándo | Qué mostrar |
|---|---|---|---|
| 400 | `ITEMS_REQUERIDOS` | Sin ítems | Deshabilitar el botón sin bultos |
| 400 | `MERCANCIA_DESCONOCIDA` | Ítem sin código ni descripción, descripción sin parecido (`detalle.item`, `detalle.descripcion`), o código desconocido o inactivo (`detalle.item`, `detalle.campo = "codigo_mercancia"`; D-1019-1: Cotifly no llama al cubicador) | El mensaje; marcar el ítem y pedir que escoja la mercancía |
| 400 | `MERCANCIA_NO_CUBICABLE` | D-1019-1: código con `cubicable: false` (`detalle.item`, `detalle.campo = "codigo_mercancia"`). Cotifly no llama al cubicador | Marcar el ítem y pedir otra mercancía |
| 400 | `DIMENSIONES_INVALIDAS` | HU-023 (#176) y D1 (2026-10-16): un ítem sin `peso_kg` o con `peso_kg` ≤ 0, o con `largo_cm`, `ancho_cm` o `alto_cm` fuera de 0,1 a 999.999. `detalle.item` = índice base 0 del ítem y `detalle.campo` el campo. Cotifly no llama al cubicador | Marcar ese campo del ítem |
| 400 | `POSICION_INVALIDA` | D1 (2026-10-16): `cantidad` fuera de 1 a 100.000 (`detalle.item`, `detalle.campo = "cantidad"`). Cotifly no llama al cubicador | Marcar la cantidad del ítem |
| 400 | `DIMENSIONES_INVALIDAS`, `POSICION_INVALIDA`, `CUBICADOR_PETICION_RECHAZADA` | D1 (2026-10-16): el cubicador respondió 422 (validación de su esquema). Con `detalle.item` si señala una mercancía; `CUBICADOR_PETICION_RECHAZADA` si no. Antes era 503 `CUBICADOR_RESPUESTA_INVALIDA` | El mensaje; revisar la carga |
| 400 | `MERCANCIA_DESCONOCIDA`, `MERCANCIA_NO_CUBICABLE`, `DIMENSIONES_INVALIDAS`, `MERCANCIAS_REQUERIDAS` | Rechazo del cubicador (por ejemplo, con el catálogo caído). Las de mercancía traen `detalle.item` cuando se sabe cuál es | El mensaje; marcar el ítem si viene `detalle.item` |
| 400 | `CUBICACION_DEMASIADO_GRANDE` | Vigente (HU-023, #176): más de 10.000 unidades en total (Cotifly no llama al cubicador) | El mensaje: partir la carga |
| 503 | `CUBICADOR_NO_DISPONIBLE` | Cubicador sin configurar o caído (un POST sin respuesta se reconsulta antes de rendirse; nunca se repite) | El mensaje y un botón Reintentar |
| 503 | `CUBICADOR_NO_AUTORIZADO`, `CUBICADOR_RESPUESTA_INVALIDA` | Llave rechazada o respuesta fuera del contrato (un 422 del `POST /recomendaciones` ya no cae aquí: es 400, ver arriba) | "Avisa a soporte" |
| 503 | `CUBICADOR_OCUPADO` | Opción C (2026-10-19): el cubicador está calculando otras cargas (con `Retry-After: 10`). Ver §8.12.1 | "El cubicador está ocupado" y Reintentar |

#### 8.12.1 Cubicación lenta: 202 y `GET /web/cubicacion/{id}` (opción D, E2E 2026-10-16)

El motor del cubicador tarda **26-31 s** con cargas grandes (1.100 unidades) y bloquea su API mientras calcula. Para no colgar la
pantalla, `POST /web/cubicacion` ahora tiene **dos respuestas buenas**:

- **200** (como siempre) si el cubicador termina dentro de la espera síncrona (`Cubicador:EsperaSincronaCubicacionSegundos`, **8 s** por
  defecto). La forma no cambia.
- **202** si no termina a tiempo. El cálculo **sigue en segundo plano** (aunque el navegador corte la conexión) y la respuesta trae el
  `id` para consultarlo, con `Location: /web/cubicacion/{id}` y `Retry-After: 2`.

Los 400 de los ítems (`ITEMS_REQUERIDOS`, `DIMENSIONES_INVALIDAS`, `POSICION_INVALIDA`) se validan **antes** y siempre llegan de forma
síncrona.

<!-- respuesta: POST /web/cubicacion 202 -->
```json
{ "id": "7d5c2c9e-1b8a-4f0e-9c3a-2f6b8e1d4a90", "estado": "en_proceso", "consultar_en": "/web/cubicacion/7d5c2c9e-1b8a-4f0e-9c3a-2f6b8e1d4a90" }
```

`GET /web/cubicacion/{id}` responde 200 con `estado`:

- `en_proceso`, con `Retry-After: 2`;
- `completada`, con `resultado` (la misma forma del 200 de `POST /web/cubicacion`);
- `fallida`, con `error` `{codigo, mensaje, detalle?}` y los mismos códigos de la tabla de arriba (por ejemplo `MERCANCIA_NO_CUBICABLE`,
  `DIMENSIONES_INVALIDAS` con `detalle.item`, `CUBICADOR_NO_DISPONIBLE` o `CUBICADOR_OCUPADO`).

`resultado` y `error` solo vienen cuando aplican.

<!-- respuesta: GET /web/cubicacion/{id} 200 -->
```json
{ "id": "7d5c2c9e-1b8a-4f0e-9c3a-2f6b8e1d4a90", "estado": "en_proceso" }
```

<!-- respuesta: GET /web/cubicacion/{id} 200 -->
```json
{
  "id": "7d5c2c9e-1b8a-4f0e-9c3a-2f6b8e1d4a90",
  "estado": "completada",
  "resultado": {
    "resultado": "ASIGNADO",
    "motivo_sin_seleccion": null,
    "mensaje_sin_seleccion": null,
    "condicionada": false,
    "vehiculos": [
      { "codigo_vehiculo": "3S3", "nombre_vehiculo": "Tractocamión 3S3", "codigo_carroceria": "FURGON", "nombre_carroceria": "Furgón", "peso_total_kg": 16000, "volumen_total_m3": 62.5, "porcentaje_llenado_volumen": 81.25, "porcentaje_llenado_peso": 47.06,
        "numero_vehiculo": 1, "alerta_bajo_llenado": false, "heterogenea": false, "mercancias": [] }
    ],
    "mercancias_condicionadas": [],
    "advertencias": [],
    "extradimension": null,
    "mercancias": [ { "codigo_mercancia": "CAFE_EN_ESTIBAS", "nombre": null, "descripcion": null, "sugerida": false } ]
  }
}
```

<!-- respuesta: GET /web/cubicacion/{id} 200 -->
```json
{
  "id": "7d5c2c9e-1b8a-4f0e-9c3a-2f6b8e1d4a90",
  "estado": "fallida",
  "error": { "codigo": "MERCANCIA_NO_CUBICABLE", "mensaje": "Una de las mercancías no se puede cubicar: revisa el tipo de mercancía." }
}
```

<!-- respuesta: GET /web/cubicacion/{id} 404 -->
```json
{ "error": "La cubicación no existe, ya venció o no es tuya: vuelve a calcular el cubicaje." }
```

**Cupo del cubicador (opción C).** Cotifly hace **una sola llamada a la vez** al cubicador, compartida con la captura, el recálculo y la
licitación (`Cubicador:MaximoConcurrente = 1`). Si otra carga ocupa el cubicador más de `Cubicador:EsperaMaximaCupoSegundos` (60 s), la
cubicación termina con `CUBICADOR_OCUPADO`: **503 con `Retry-After: 10`** si pasa dentro de la espera síncrona, o `fallida` con ese código en
el `GET`. También responde 503 `CUBICADOR_OCUPADO` si hay demasiadas cubicaciones en curso (`Cubicador:MaximoCubicacionesEnMemoria`).

<!-- respuesta: POST /web/cubicacion 503 -->
```json
{
  "error": {
    "codigo": "CUBICADOR_OCUPADO",
    "mensaje": "El cubicador está calculando otras cargas en este momento: intenta de nuevo en unos segundos."
  }
}
```

**Cómo lo maneja el frontend:**

1. `POST /web/cubicacion`. Con **200**, pintar el resultado como hoy. Con **202**, mostrar "Calculando… el cubicador puede tardar hasta
   un minuto" y guardar `consultar_en`.
2. Consultar `GET consultar_en` cada **2-3 s** (el `Retry-After`), con un tope de **4 minutos** (una cubicación puede esperar 60 s el cupo
   del cubicador, 90 s su respuesta y 65 s la reconsulta: unos 217 s con la configuración por defecto). Mientras sea `en_proceso`, seguir
   mostrando "Calculando…". El comercial puede seguir editando: si cambia la carga, descartar la consulta anterior y volver a calcular.
3. Con `completada`, pintar `resultado` igual que el 200. Con `fallida`, tratar `error` igual que un error del `POST`: los 400 del cubicador
   (marcar el ítem si trae `detalle.item`) o "no disponible" con un botón **Reintentar**.
4. Con **404**, la cubicación venció, no es del comercial o se perdió porque Cotifly se reinició (se guardan en memoria 15 min): volver a
   calcular sin mostrar un error técnico.
5. Con **503 `CUBICADOR_OCUPADO`**, esperar los segundos de `Retry-After` y ofrecer **Reintentar**.

| HTTP | Código | Cuándo | Qué mostrar |
|---|---|---|---|
| 202 | — | El cubicador no terminó en la espera síncrona (8 s) | "Calculando…" y consultar `GET /web/cubicacion/{id}` cada 2-3 s |
| 503 | `CUBICADOR_OCUPADO` | El cubicador está calculando otras cargas y no se liberó a tiempo, o hay demasiadas cubicaciones en curso. Trae `Retry-After: 10` | "El cubicador está ocupado" y Reintentar tras el `Retry-After` |
| 404 (`GET`) | — (`ErrorWeb`) | La cubicación no existe, venció, es de otro usuario o se perdió al reiniciar Cotifly | Volver a calcular |

**Qué cambia para el frontend de hoy** (que ya usa `/web/cubicacion`):

- La respuesta **cambia de forma**: `sugerencia` y `sugerencia_flota` desaparecen. Ahora es `vehiculos[]`, y `extradimension` va en
  la raíz con la estructura del cubicador, `{hay_extradimension, medidas_estandar, piezas[]}` (ver la última viñeta), o `null`.
- No vuelve a llegar la clase de viaje ni la clase de contenedor sugeridas: si el formulario las prellenaba desde aquí, quitarlo.
- Cada ítem debe llevar `codigo_mercancia`. La `descripcion` sola sigue funcionando mientras llega el desplegable, pero si no se parece
  a ninguna mercancía del catálogo ahora es un 400, y un ítem sin código ni descripción también.
- **Plan B del corte** (solo si el frontend no alcanza a pintar `vehiculos[]` para el 16): el servidor puede encender
  `Cubicacion:IncluirSugerenciaLegada` (apagada por defecto) y la respuesta agrega `sugerencia` con la forma anterior
  (`unidad_id`, `unidad_nombre`, `banda`, `factor_utilizacion`, `config_vehiculo`, `carroceria`, `clase_viaje`, `clase_contenedor`)
  derivada del **primer** vehículo; `sugerencia_flota` no vuelve. Sin vehículos, el campo no viene. Es temporal: se apaga cuando el
  frontend use `vehiculos[]`.
- Los códigos de error cambian: `CUBICACION_NO_DISPONIBLE` (400) se retiró; ahora llegan los del cubicador (tabla de arriba), con la
  caída en **503**. HU-023 (#176): `CUBICACION_DEMASIADO_GRANDE` (400) está **vigente**, con el tope del cubicador (10.000 unidades).
- HU-023 (#176): `extradimension` vuelve a la estructura del cubicador (`hay_extradimension`, `medidas_estandar`, `piezas[]` con
  `indice_posicion` y `excesos[]`): la forma provisional `{extradimensionado, dimensiones, detalle}` se retira.

### 8.13 Editar la solicitud: `GET` y `PUT /web/cotizaciones/{folio}/solicitud` (HU-028, #186)

El comercial edita la solicitud que ya creó: **todo menos el cliente, el tipo (spot/licitación) y la línea de negocio de cada ruta**.
Puede cambiar rutas (agregar, quitar, cambiar origen, destino y ciudades), mercancías, condiciones, conexos, clase de viaje, horas,
valores, deadline, notas y moneda. Decisión en [ADR-021](../../backend-dotnet/specs/decisiones/ADR-021-edicion-de-la-solicitud.md).

**Modo según el estado** (lo decide el servidor y el `GET` lo anuncia en `edicion.modo`):

| Modo | Cuándo | Qué hace | Respuesta del `PUT` |
|---|---|---|---|
| `en_sitio` | Nunca llegó a NEXO (`error_envio`, también el rechazo definitivo, o `pendiente_envio` abandonado) | Edita la misma cotización y la envía | 200 |
| `reemplazo` | Está en NEXO, sin entregar al cliente y sin resultado (también en `pendiente_costeo`) | Rechaza la solicitud anterior en NEXO, aplica la edición y la captura de nuevo **en el mismo folio**; el PDF queda desactualizado | 200 |
| `nueva_version` | Ya entregada al cliente (`enviada`/`reenviada`) o con resultado | Crea la `-vN` con lo editado; la original no cambia ni se rechaza en NEXO | 201 + `Location` |
| (no permitida) | Licitación (fase 2), cotización del chat, envío o cubicación en curso | Nada | 409 `ESTADO_NO_PERMITE` |

**Flujo de la pantalla "Editar solicitud":**

1. `GET .../solicitud` y guardar el `ETag`. Si `edicion.permitida` es `false`, mostrar `edicion.mensaje` y no abrir el formulario.
2. Prellenar el formulario de captura por rutas (el mismo de `POST /web/solicitudes-costeo`) con la respuesta: cada ruta trae
   `linea_solicitud_id`, los municipios con id y nombre, las mercancías con `codigo_mercancia` y los conexos con el id del catálogo.
   Cliente, tipo y la línea de negocio de cada ruta guardada van en **solo lectura** (`bloqueados`, `campos_bloqueados`). Una ruta nueva
   solo ofrece las líneas de `lineas_negocio_disponibles`.
3. Antes de guardar, mostrar el modo y los `edicion.avisos` (por ejemplo "se reemplazará la solicitud en NEXO", "la Mesa está costeando:
   se pierde su trabajo", "se creará la versión -vN") y pedir un `motivo` opcional.
4. `PUT .../solicitud` con el modelo editado (el JSON del `GET` sirve tal cual: los campos que no son de entrada se ignoran),
   `If-Match` con el `ETag`, `Idempotency-Key` y `modo_esperado` con el modo que se mostró.
5. 200: refrescar la misma cotización. 201: navegar a la `-vN` (`folio` de la respuesta). 409 `CONFLICTO_CONCURRENCIA` o
   `ESTADO_NO_PERMITE` (`MODO_CAMBIO`): recargar y confirmar de nuevo. 400: marcar los campos de `detalle.errores` (o `detalle.campo`).
   503 con `detalle.motivo = RECHAZO_NEXO_FALLIDO`: "NEXO no respondió; no se cambió nada", recargar (la versión subió) y reintentar.

Reglas que conviene saber:

- Las rutas que se conservan **no se reordenan** (`RUTAS_REORDENADAS`); las nuevas pueden ir en cualquier posición. Quitar una ruta con
  margen ajustado deja su ajuste en el historial sin ruta (aviso `MARGEN_RUTA_QUITADA`).
- El margen negociado de una ruta que se conserva **sobrevive**; una ruta nueva toma el margen sugerido al enviarse.
- Solo se vuelven a pedir los camiones al cubicador de las rutas nuevas o con el peso, las mercancías o la clase de viaje cambiados
  (`rutas_replanificadas`); las demás conservan sus camiones, también el vehículo que la Mesa haya cambiado.
- `moneda` ausente conserva la guardada; si cambia, se fija la TRM del día.
- Si el cuerpo no cambia nada, 200 con `sin_cambios: true` y nada se toca.

<!-- respuesta: GET /web/cotizaciones/{folio}/solicitud 200 -->
```json
{
  "folio": "PCN-900555111-DG-09102026-001",
  "version": 1,
  "version_negocio": 7,
  "bloqueados": { "tipo": "spot", "nit_cliente": "900555111", "cliente_nombre": "LONGYIN S.A.S.", "origen_cliente": "cliente" },
  "campos_bloqueados": ["tipo", "nit_cliente", "cliente_nombre", "origen_cliente", "rutas[].linea_negocio"],
  "edicion": {
    "modo": "reemplazo",
    "permitida": true,
    "motivo_no_permitida": null,
    "mensaje": null,
    "avisos": [
      { "codigo": "REEMPLAZA_SOLICITUD_NEXO", "mensaje": "La solicitud actual se rechaza en Nexo y se captura de nuevo en el mismo folio." },
      { "codigo": "PIERDE_TRABAJO_MESA", "mensaje": "La Mesa de Costeo está costeando esta solicitud: se pierde el trabajo que lleve y vuelve a empezar." }
    ]
  },
  "deadline": "2026-10-15T22:00:00+00:00",
  "notas": null,
  "moneda": "COP",
  "linea_negocio": "Importacion",
  "lineas_negocio_disponibles": ["Importacion"],
  "rutas": [
    {
      "linea_solicitud_id": "0c6d1f0e-7d1c-4f5e-9a3b-2e4f6a8b0c1d",
      "linea_orden": 1,
      "linea_negocio": "Importacion",
      "linea_negocio_bloqueada": true,
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
      "ciudad_levante_municipio_id": null,
      "ciudad_levante_contenedor": null,
      "horas_cargue": 4,
      "horas_descargue": 6.5,
      "valor_levante_contenedor": null,
      "valor_mercancia": 250000000,
      "peso_kg": 28500.5,
      "mercancias": [
        { "codigo_mercancia": "CAFE_EN_ESTIBAS", "nombre_mercancia": "Estiba de café", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 1424.95 }
      ],
      "servicios_conexos": [ { "id_servicio_conexo": "CX-1", "nombre": "Cargue", "cantidad": 1 } ],
      "margen_aplicado": 0.15,
      "origen_margen": "politica_linea",
      "margen_negociado": false
    }
  ]
}
```

<!-- esquema: EdicionSolicitudWeb -->
```json
{
  "modo_esperado": "reemplazo",
  "motivo": "El cliente cambió el horario de cargue",
  "deadline": "2026-10-15T17:00:00-05:00",
  "rutas": [
    {
      "linea_solicitud_id": "0c6d1f0e-7d1c-4f5e-9a3b-2e4f6a8b0c1d",
      "origen_municipio_id": 76109, "origen": "Buenaventura",
      "destino_municipio_id": 11001, "destino": "Bogotá D.C.",
      "tipologia": "Contenedor", "clase_viaje": "Contenedor", "clase_contenedor": "CNT40.HC",
      "exclusivo": true, "consolidado": false,
      "retorna_contenedor": true, "ciudad_devolucion_municipio_id": 76109, "ciudad_devolucion_contenedor": "Buenaventura",
      "horas_cargue": 9, "horas_descargue": 6.5, "valor_mercancia": 250000000, "peso_kg": 28500.5,
      "mercancias": [
        { "codigo_mercancia": "CAFE_EN_ESTIBAS", "cantidad": 20, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 1424.95 }
      ],
      "servicios_conexos": [ { "id_servicio_conexo": "CX-1", "cantidad": 1 } ]
    },
    {
      "linea_negocio": "Importacion",
      "origen_municipio_id": 76109, "origen": "Buenaventura",
      "destino_municipio_id": 76001, "destino": "Cali",
      "tipologia": "CargaSuelta", "clase_viaje": "Carga Suelta",
      "peso_kg": 2000,
      "mercancias": [ { "codigo_mercancia": "BEBIDAS", "cantidad": 10, "largo_cm": 120, "ancho_cm": 100, "alto_cm": 150, "peso_kg": 200 } ]
    }
  ]
}
```

<!-- respuesta: PUT /web/cotizaciones/{folio}/solicitud 200 -->
```json
{
  "modo": "reemplazo",
  "folio": "PCN-900555111-DG-09102026-001",
  "folio_origen": "PCN-900555111-DG-09102026-001",
  "sin_cambios": false,
  "cotizacion": {
    "id": "5b0f8a3e-2c1d-4e6f-9a7b-3c2d1e0f9a8b",
    "folio": "PCN-900555111-DG-09102026-001",
    "version": 1,
    "nit_cliente": "900555111",
    "origen_cliente": "cliente",
    "origen_margen": "politica_linea",
    "cliente": "LONGYIN S.A.S.",
    "linea_negocio": "Importacion",
    "precio_final": null,
    "margen_aplicado": 0.15,
    "moneda": "COP",
    "trm_aplicada": 0,
    "estado_costeo": "pendiente_costeo",
    "motivo_estado_costeo": null,
    "estado_envio": "sin_enviar",
    "ganada": null,
    "motivo_rechazo": null,
    "pdf_url": null,
    "created_at": "2026-10-09T15:20:11.532+00:00",
    "deadline": "2026-10-15T22:00:00+00:00",
    "score_prioridad": 0.5
  },
  "avisos": [
    { "codigo": "REEMPLAZA_SOLICITUD_NEXO", "mensaje": "La solicitud actual se rechaza en Nexo y se captura de nuevo en el mismo folio." },
    { "codigo": "PIERDE_TRABAJO_MESA", "mensaje": "La Mesa de Costeo está costeando esta solicitud: se pierde el trabajo que lleve y vuelve a empezar." }
  ],
  "rutas_replanificadas": [2],
  "rutas_agregadas": [2],
  "rutas_quitadas": [],
  "solicitud_nexo_reemplazada": "descartada"
}
```

<!-- respuesta: PUT /web/cotizaciones/{folio}/solicitud 400 -->
```json
{
  "error": {
    "codigo": "CAMPO_NO_EDITABLE",
    "mensaje": "La línea de negocio de una ruta no se cambia: la ruta 1 es 'Importacion'.",
    "detalle": {
      "campo": "rutas[0].linea_negocio",
      "campos": ["rutas[0].linea_negocio"],
      "errores": [
        { "ruta": 0, "campo": "rutas[0].linea_negocio", "codigo": "CAMPO_NO_EDITABLE", "mensaje": "La línea de negocio de una ruta no se cambia: la ruta 1 es 'Importacion'." }
      ]
    }
  }
}
```

<!-- respuesta: PUT /web/cotizaciones/{folio}/solicitud 503 -->
```json
{
  "error": {
    "codigo": "NEXO_NO_DISPONIBLE",
    "mensaje": "No se pudo rechazar en Nexo la solicitud anterior de PCN-900555111-DG-09102026-001: la edición no se aplicó y no se creó otra solicitud. Intenta de nuevo en unos minutos (recarga la solicitud: su versión cambió).",
    "detalle": { "motivo": "RECHAZO_NEXO_FALLIDO", "solicitud_nexo_id": "3f2b8c1e-0000-4000-8000-000000000001", "version_negocio": 8, "causa": "COSTEO_NEXO_NO_DISPONIBLE" }
  }
}
```

| HTTP | Código | Cuándo | Qué mostrar |
|---|---|---|---|
| 400 | `CAMPO_NO_EDITABLE` | Cambia el cliente, el tipo o la línea de una ruta guardada, o una ruta nueva usa una línea que la solicitud no tiene | Restaurar el valor (`detalle.campo`) |
| 400 | `SOLICITUD_COSTEO_INVALIDA` | Reglas de la captura, `RUTA_INEXISTENTE`, `RUTA_REPETIDA` o `RUTAS_REORDENADAS` en `detalle.errores` | Marcar los campos |
| 403 | `NEXO_SIN_PERMISO` | NEXO no deja rechazar la solicitud anterior | "Pide el rol en NEXO"; nada cambió |
| 409 | `ESTADO_NO_PERMITE` | `LICITACION`, `SIN_FORMULARIO`, `ENVIO_EN_CURSO`, `CUBICACION_EN_CURSO` o `MODO_CAMBIO` en `detalle.motivo` | El mensaje; con `MODO_CAMBIO`, recargar |
| 409 | `CONFLICTO_CONCURRENCIA` | El `If-Match` ya no es el vigente | Recargar |
| 428 | `PRECONDICION_REQUERIDA` | Falta `If-Match` | (error de integración) |
| 503 | `NEXO_NO_DISPONIBLE` (`RECHAZO_NEXO_FALLIDO`) | El rechazo de la solicitud anterior falló: no se capturó otra | "NEXO no respondió; no se cambió nada" y Reintentar tras recargar |

**`re-solicitar` con `datos_carga` queda obsoleto** (`deprecated` en el contrato): para cambiar la carga se usa este `PUT`. Mientras el
frontend migra, `datos_carga` se sigue aceptando con tres defectos corregidos: las mercancías conservan `codigo_mercancia` y
`nombre_mercancia`, las ciudades de devolución y de levante aceptan `ciudad_devolucion_municipio_id` y `ciudad_levante_municipio_id`, y un
cambio de `clase_viaje` vuelve a pedir los camiones al cubicador. `GET .../nueva-version` sigue disponible, pero para prellenar usar el
`GET .../solicitud`.
