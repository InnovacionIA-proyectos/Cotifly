# Pendientes de backend para Duban

> **Última revisión: 2026-10-19** contra `origin/QA` hasta #178. Hechos: punto 1 (CORS, #178) y 7.5 (listado regional, #154). También entró en QA HU-018.1 (#155): `CotizacionResumen` trae `deadline`, `score_prioridad` y `comercial`, pero sigue sin existir el endpoint de detalle (7.1).

**Fecha:** 2026-10-16 · **Revisado contra:** `origin/QA` hasta #177 · **Repo:** `Cotifly`.

Solo lo que le toca a backend, de lo más urgente a lo menos urgente. Incluye (punto 8) el rechazo de una línea en NEXO, que antes estaba en un archivo aparte. El contexto completo está en `docs/backlog/2026-10-14_HU_Correcciones_Nuevas_Cotifly.md`.

| Prioridad | Qué significa |
|---|---|
| 🔴 Urgente | Bloquea pruebas hoy |
| 🟠 Alta | Esta semana; afecta funciones que ya se están construyendo |
| 🟡 Media | Con dependencias o coordinado con otros equipos |
| ⚪ Baja | Funciones que faltan en el backend; no bloquean, pero el frontend las va a pedir |

El punto 8 es 🟡 Media pero va al final porque depende de que NEXO publique el contrato.

---

## ✅ 1. CORS no permite `If-Match` ni `Idempotency-Key` — HECHO (#178, 2026-10-16)

> **Revisado el 2026-10-19 en `origin/QA`:** `EncabezadosCors = ["Authorization", "Content-Type", "If-Match", "Idempotency-Key"]` y `.WithExposedHeaders("ETag", "Location", "Retry-After", "Content-Disposition")` (`clsSeguridadHttp.cs:150-185`). Lo de abajo queda como registro.


- [x] Agregar `If-Match` e `Idempotency-Key` a `EncabezadosCors` en `src/Cotifly.Api/Comun/clsSeguridadHttp.cs:145`. Hoy es `["Authorization", "Content-Type"]`.
- [x] Opcional: `.WithExposedHeaders("ETag")` en la política. El frontend ya lee `row_version` del cuerpo, así que no depende del encabezado.

**Evidencia** (2026-10-16, backend local en `localhost:8083`):
```
curl -i -X OPTIONS "http://localhost:8083/web/cotizaciones/TEST/costeo/servicios-conexos" -H "Origin: http://localhost:4200" -H "Access-Control-Request-Method: PUT" -H "Access-Control-Request-Headers: If-Match, Content-Type"

HTTP/1.1 204 No Content
Access-Control-Allow-Headers: Authorization,Content-Type
```
- **Efecto:** el navegador bloquea en el preflight cualquier petición con `If-Match` o `Idempotency-Key`; el servidor nunca la recibe.
- **Qué afecta:** `POST`/`PUT .../costeo/servicios-conexos`, `PUT .../costeo/margen`, `POST .../costeo/re-solicitar` y `POST /web/solicitudes-costeo` con `Idempotency-Key` (#173).
- **Ya bloquea las pruebas del frontend en local:** el frontend llama directo a `http://localhost:8083`, sin proxy. En producción todavía no está definido si backend y frontend comparten origen.

---

## 🟠 2. Confirmar `Captura:AceptarVehiculoCliente` en los ambientes

- [ ] Confirmar si algún ambiente desplegado la prende por variable de entorno. En `origin/QA` solo aparece comentada en `.env.example` (`# Captura__AceptarVehiculoCliente=false`) y no está en ningún `appsettings`.
- [ ] Decidir si se prende mientras el cubicador real no esté conectado.
- **Por qué importa:** con la bandera apagada, el backend ignora `tipo_vehiculo` y `carroceria` del cliente. La pantalla del frontend para digitar el vehículo (respaldo de HU-021-FE.2, #70) no tiene efecto.
- También apagadas por defecto desde #175: `Nexo__EnviarCodigosCubicador` y `Nexo__EnviarGrupoRutaId`. Hay que confirmar su valor en cada ambiente.

---

## 🟠 3. HU-010.5 — Filtro por año y mes en `GET /web/metricas/regional`

**Issue nueva dentro de la milestone HU-010.** Es lo que necesita el selector año/mes del tablero del gerente (HU-010.3, clientepro-frontend #51).

El cambio ya está escrito y guardado como parche: **`docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`**, sobre `QA` (`cf744ff`). Se comprobó que aplica limpio sobre `QA` con #175. Son 9 archivos: unas 45 líneas de código, 90 de pruebas y 50 de documentación.

**Qué hace:**
- `?anio=2026` → el año completo; `?anio=2026&mes=10` → ese mes completo.
- Si llega `anio`, reemplaza a `desde`/`hasta`; no se combinan. Sin `anio` ni `mes`, todo funciona igual que antes.
- 400 si `mes` no está entre 1 y 12, si llega `mes` sin `anio` o si el año no es válido.
- `/web/metricas/comercial` no cambia.

**Checklist:**
- [ ] Revisar el cambio y confirmar que se sube pese al freeze.
- [ ] Rama desde `QA` y `git apply --3way docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`.
- [ ] Correr las pruebas: unitarias, de API y de aceptación (`metricas_tablero.feature`).
- [ ] Revisar si hace falta anotar la extensión en ADR-016 §5.
- [ ] PR a `QA` (`Part of HU-010`) y avisarle al frontend (#51).

---

## 🟡 4. Cambio de la base de identidad a `dbSolucionesTI_Prueba` (#172) — la parte de backend

Va junto con el cambio del rol `comercial` de 34 a 32. Son tres partes y van en una sola ventana: **la base** (el DBA corre `db/scripts/operacion/corregir_rol_comercial_32.sql`), **la configuración** (esta parte) y **el frontend** (Sara, `cotifly-roles.ts`).

- [ ] Coordinar la ventana: cada ambiente apunta `ConnectionStrings__Identidad` a `dbSolucionesTI_Prueba`, y en la misma ventana, con la API detenida, el DBA corre `revincular_identidad_dbSolucionesTI_Prueba.sql` y `corregir_rol_comercial_32.sql`.
- [ ] Confirmar con alguien que tenga acceso a `dbSolucionesTI_Prueba` que ahí el 32 es `ClienteProComercial` y el 31 es `ClienteProGerencial`. Hoy solo lo dice el script.
- **Cuidado con el orden:** en un ambiente que siga con la identidad vieja (`dbsolucionesti`), el 32 es *proveeduría*.
- **Estado al 2026-10-16:** la compartida tiene `comercial=34, gerente_comercial=31`, y el `.env` local ya apunta a `dbSolucionesTI_Prueba`.

---

## 🟡 5. Usar las columnas del `03`

- [ ] Mapear `CotizacionVehiculosLineaSolicitud.ordenVehiculo`, en lugar del desfase de 1 ms en `createdAt` (`clsVehiculoLineaSolicitud.OrdenCreacion`).
- [ ] Mapear `CotizacionServiciosConexos.idServicioConexoCatalogo`, en lugar de resolver el conexo por nombre (`clsResolutorConexosCatalogo`).
- Las columnas ya existen en la compartida desde el 2026-10-14. Hasta que se mapeen, las filas nuevas quedan en NULL.

## 🟡 6. Columnas y tabla ya creadas o propuestas, sin usar

- [ ] `CotizacionServiciosConexos.categoriaServicioConexo` (script `05`): ya está en la compartida desde el 2026-10-16. Guardar ahí la categoría al capturar o importar los conexos, como propone el encabezado del `05`.
- [ ] `CapturasIdempotentes` (script `06`): cuando el backend pase la idempotencia de memoria a la tabla, avisarle a Sara para que la aplique. Hoy no hace falta.

---

## ⚪ 7. Funciones que faltan en el backend (salieron de construir el frontend, 2026-10-16)

Ninguna es un error: son funciones que el contrato de hoy no tiene y que las HU del frontend necesitan. Si no se hacen, hay que recortar el alcance de esas HU.

| # | Qué falta | Lo necesita | Situación hoy |
|---|---|---|---|
| 7.1 | **Endpoint de detalle por folio** (cabecera, rutas, mercancías, camiones, conexos, margen y precio en una llamada) | HU-021-FE.7 (#75), "Ver detalle", para poder recargar la página | No existe `GET /web/cotizaciones/{folio}`; el frontend combina `mis-cotizaciones` + `GET .../costeo`. Cliente, NIT, `estado_envio` y `ganada` solo vienen en el listado |
| 7.2 | **`DatosNuevaVersion` con mercancías, conexos y condiciones por ruta** | HU-021-FE.8 (#76) | `RutaNuevaVersion` solo trae línea, origen, destino y margen. Hay un trabajo pausado por el freeze ("DatosNuevaVersion ampliado", HU-004.4) en el stash de Sara |
| 7.3 | **Nueva versión con cambios para cotizaciones de varias rutas** (`datos_carga` por ruta) | HU-021-FE.8 (#76) | `DatosCargaWeb` es una sola carga sin ruta; con más de una ruta responde 409 `ESTADO_NO_PERMITE` (`VARIAS_RUTAS`) |
| 7.4 | **Total de la solicitud en el asunto o el cuerpo del correo** | HU-021-FE.8 (#76) | El asunto por defecto es `Cotización {folio} — Botero Soto` y el cuerpo es fijo; el precio solo va en el PDF. Confirmar con negocio si se quiere |
| 7.5 | ✅ **HECHO (#154, HU-016.1, en QA):** listado regional de cotizaciones del gerente (`GET /web/cotizaciones/regional`) | "Todas las cotizaciones" del Gerente Comercial | Misma forma que `mis-cotizaciones` más `comercial` por fila; solo `gerente_comercial`; la regional se resuelve en el servidor; 409 `REGIONAL_NO_ASIGNADA` sin regional |
| 7.6 | **`clases_contenedor` en `GET /web/catalogos-flete`** (`OpcionCatalogo`; `valor` = `etiqueta` = `descripcion` de NEXO, como `clases_viaje`) | HU-021-FE.11 (#79): quitar la lista fija del frontend | El backend ya lee `GET /api/catalogos/clases-contenedor` de NEXO, pero solo para validar; `CatalogosFlete` trae solo `tipos_vehiculo`, `tipos_carroceria` y `clases_viaje`. Era la parte B de HU-023.1 en el plan del Sprint 5, y no está hecha. HU-023.1 ya quitó la lista fija de 5 códigos del validador, así que hoy el backend solo acepta la `descripcion` exacta de NEXO (o la lista G15), y con el catálogo de NEXO vacío responde 503. **No hay un error activo:** Sara verificó el 2026-10-16 que las `descripcion` de NEXO son exactamente `CNT20.ST`, `CNT20.RE`, `CNT20.IT`, `CNT40.HC` y `CNT40.RE`, iguales a la lista fija del frontend. Queda el riesgo de que se desincronicen si NEXO cambia su catálogo. Además, `openapi.yaml` (`SolicitudCosteoWeb.clase_contenedor`) todavía menciona los 5 códigos fijos: hay que actualizarlo |
| 7.7 | **"Margen promedio" contra la política** (HU-016.1, parte B) | HU-021-FE.11 (#79), tarjeta "Margen promedio" de Mis cotizaciones | **Primero decide negocio:** Opción A, un endpoint nuevo accesible al Comercial que liste `MargenesPolitica` (línea y margen) de las líneas activas para que el frontend calcule el promedio; u Opción B, cambiar `desviacion_promedio_margen` (ADR-016 la define como "solo ajustes negociados"). Hoy `GET /web/admin/lineas-negocio` responde 403 al Comercial, y `/web/metricas/comercial` solo da escalares. Tampoco está decidido si el promedio por ruta es simple o ponderado por costo |

---

## 🟡 8. Rechazo de una línea con motivo en la Mesa de Costeo de NEXO

> ⚠ **Contrato con NEXO aún sin cerrar:** falta que NEXO lo implemente. Las decisiones de negocio ya están tomadas (2026-10-16). Esta sección dice qué tiene que cambiar Cotifly y qué le pedimos a NEXO. **No construir nada hasta que NEXO publique el contrato.** La parte de base de datos le toca a Sara.

### La decisión (Sara, 2026-10-16)

En la Mesa de Costeo de NEXO, el Administrador de Tarifas puede **rechazar UNA línea** con un **motivo obligatorio** ("no se va a cotizar por X"):
- **No frena la cotización:** se puede entregar, y salen el PDF y el Excel.
- **La línea rechazada no suma al total y no lleva precio.**
- **El motivo lo ve solo el comercial,** en su pantalla de costeo y en la Mesa. El PDF y el Excel no lo muestran.

#### Decisiones de Sara (2026-10-16)
1. **PDF y Excel:** la línea rechazada sale marcada como **"Sin costeo"** (o un texto parecido), **sin precio y SIN el motivo**.
2. **Deshacer el rechazo:** solo se puede mientras la cotización siga en la Mesa de Costeo. Una vez **entregada**, ya no.
3. **Si se rechazan TODAS las líneas:** la cotización se le envía igual al comercial, con el motivo de cada línea.

### Cómo está hoy Cotifly (verificado en el código)

| Situación | Qué hace hoy | Problema con el rechazo |
|---|---|---|
| Línea **borrada** en NEXO (#134) | El camión queda `descartada` (`clsVehiculoLineaSolicitud.DescartarPorLineaBorrada`): pierde sus costos de NEXO, no cuenta en el estado ni en los totales de la ruta (`clsLineaSolicitud.VehiculosQueCuentan`) y no recibe conexos | Es lo más parecido, pero no guarda motivo. Además, si **todos** los camiones de una ruta están descartados, "cuentan todos" y la ruta queda **sin flete** |
| Línea **sin flete** en una cotización **entregada** | `clsDerivacionEstadoCosteo.DerivarLinea` la deja `entregada` sin costo ("NEXO la entregó sin esa línea") | La ruta queda sin precio, `precio_final` en `null`, y el PDF responde 409 `COSTEO_NO_LISTO` porque exige **todas** las rutas con precio |
| Línea sin flete **antes de entregar** | `Derivar` solo da `autocosteo` si **todas** las líneas tienen flete; si no, `pendiente_costeo` | Una línea rechazada nunca tendría flete: la cotización se quedaría en `pendiente_costeo` (y el polling seguiría) hasta que NEXO la entregue |

Hoy Cotifly **no puede distinguir** una línea rechazada de una línea que todavía no se ha costeado, ni de una borrada: NEXO no le manda ninguna señal ni motivo.

### (b) Qué le pedimos a NEXO en el contrato

En `GET /api/solicitudes/{id}`, por cada línea de la solicitud (y si aplica, en `GET /api/cotizaciones/{id}`):

| Campo propuesto | Tipo | Para qué |
|---|---|---|
| `estadoLinea` (o un valor nuevo en el estado actual de la línea) | enum, con `Rechazada` | Distinguir "rechazada" de "pendiente" y de "borrada" |
| `motivoRechazo` | texto, obligatorio si está rechazada | Mostrárselo al comercial (no va en el PDF ni en el Excel, decisión 1) |
| `rechazadaAt` y quién la rechazó (opcional) | fecha y usuario | Auditoría |
| `numeroLinea` (ya pedido en NEXO#297) | int | Emparejar la línea con su camión (ya está en el contrato de captura) |

Ya decidido: deshacer el rechazo solo antes de entregar, y si se rechazan todas, la cotización se entrega igual (ver las decisiones arriba).

Falta acordar con NEXO:
- **Si la cotización de NEXO (`CotizacionDto`) omite la línea rechazada o la trae sin flete con el estado.** Hoy Cotifly toma el costo de la cotización de NEXO.
- **Cómo se ve "se rechazaron todas"** en la solicitud y la cotización de NEXO: estado `Entregada` con todas las líneas `Rechazada`, sin flete.

### (a) Qué tendría que cambiar Cotifly

#### Lectura del costeo (polling)
- [ ] Leer el estado y el motivo de cada línea de la foto de NEXO (`clsCosteoConsultadoNexo`, `clsLineaCosteoNexo` o las líneas de la solicitud).
- [ ] **Estado nuevo del camión `rechazada`**, distinto de `descartada` (que es "línea borrada", #134), y guardar `motivoRechazo`.
- [ ] **Deshacer el rechazo (decisión 2):** mientras la cotización no esté entregada, un camión `rechazada` NO es terminal. El polling lo vuelve a leer y, si NEXO lo des-rechazó, vuelve a `pendiente_costeo` y se borra el motivo. Cuando la cotización queda `entregada`, el rechazo es definitivo.
- [ ] `clsDerivacionEstadoCosteo.Derivar`: la cotización es `autocosteo` cuando todas las líneas **no rechazadas** tienen flete. Si no, se queda en `pendiente_costeo` para siempre.
- [ ] `clsResumenEstadoCosteo` y `VehiculosQueCuentan`: ignorar las `rechazada`, igual que las `descartada`. Una ruta con **todos** sus camiones rechazados queda `rechazada`, sin precio, pero **no bloquea** la cotización.
- [ ] Revisar el emparejamiento de camiones con líneas (#134): una línea rechazada sigue existiendo en NEXO, no es "línea sin dueño".

#### Base de datos (Sara, con script en `db/scripts/`)
- [ ] `CotizacionVehiculosLineaSolicitud`: agregar `rechazada` al CHECK de `estadoCosteo` (si se usa como estado), más `motivoRechazo NVARCHAR(…) NULL` y, opcional, `rechazadaAt`.
- [ ] Revisar los CHECK de `estadoCosteo` en `CotizacionLineasSolicitud` y `CotizacionesSolicitud` si el estado `rechazada` también existe en la ruta.

#### Totales, margen y precio por ruta
- [ ] `precio_final` (total) = suma de las rutas **no rechazadas**. Hoy es `null` si cualquier ruta no tiene precio.
- [ ] Una ruta rechazada: `precio_final_linea = null` con estado y motivo visibles. Ajustar el margen o agregar conexos en ella → 409 con un código claro (por ejemplo `RUTA_RECHAZADA`).
- [ ] Ruta con algunos camiones rechazados: el precio es el de los camiones vigentes. Los conexos se pegan al primer camión **no rechazado** (`clsServiciosConexosCosteoService`).
- [ ] Métricas (ADR-016): definir si el valor cotizado excluye las rutas rechazadas. Lo natural es que sí.

#### PDF de Cotifly (`POST .../costeo/pdf`)
- [ ] Cambiar la regla "todas las rutas con precio y `precio_final` = suma de `precio_final_linea`": las rechazadas no cuentan.
- [ ] **Decidido (1):** la ruta rechazada se muestra como **"Sin costeo"**, sin precio y **sin el motivo**, en el resumen del pliego y en su sección. Con una sola ruta, igual.
- [ ] **Por definir con Sara:** si se rechazaron **todas** las rutas (decisión 3), ¿se permite generar el PDF (todo "Sin costeo", total 0 o vacío) o se bloquea con un mensaje claro? Lo natural es bloquearlo: no hay nada que cotizar.

#### Excel de la licitación (`GET /web/licitaciones/{folio}/excel`)
- [ ] **Decidido (1):** la fila de una línea rechazada sale **"SIN COSTEO", sin precio y sin el motivo**, como las que no tienen costeo. En la hoja "Resumen Cotifly" no cuenta en el total. La hoja "Camiones Cotifly" igual, sin motivo.
- [ ] `Licitacion.lineas_costeadas` y `lineas_pendientes` (pantalla del comercial, no el Excel): las rechazadas no son pendientes. Hoy existe el texto "Descartada en Nexo" (`clsLicitacionService`); agregar "Rechazada en Nexo" con su motivo, porque en pantalla el comercial sí ve el motivo.

#### Pantalla de costeo y contrato web (`openapi.yaml`)
- [ ] `GET .../costeo` (lo que ve el comercial): exponer en `RutaCosteo` y `CamionCosteo` el estado `rechazada` y `motivo_rechazo`. **Cambio de enum:** el frontend tiene que agregar el valor nuevo.
- [ ] **Todas las rutas rechazadas (decisión 3):** la cotización queda `entregada`, con todas sus rutas `rechazada`, `precio_final` vacío y el motivo de cada ruta visible en el costeo. El polling se detiene, como en cualquier entrega.
- [ ] `CotizacionResumen` ("Mis cotizaciones"): el resumen del estado ignora las rechazadas; el total sale sin ellas.

#### Versiones `-vN` y re-solicitud
- [ ] **Por decidir:** ¿la `-vN` copia la ruta rechazada (y NEXO la vuelve a evaluar) o la omite? Propuesta: copiarla, avisar al comercial que fue rechazada y por qué, y dejarle quitarla.
- [ ] Re-solicitud en el mismo folio: la línea se vuelve a capturar en NEXO, y el rechazo anterior queda en la auditoría.

#### Resultado (ganada o perdida)
- [ ] Marcar la cotización como ganada con rutas rechazadas debe funcionar. Las rutas rechazadas no se envían ni se cobran.

### Lo que NO cambia
- El contrato de captura (`POST /api/captura-solicitudes`): el rechazo pasa en la Mesa de NEXO, después de la captura.
- El cubicador: no participa.

---

## Para saber, sin acción

- **El `07` (`BUSQUEDA_AGOTADA`) es el tuyo** (`db/scripts/07_motivo_busqueda_agotada.sql`, #176). Sara lo aplica en la compartida.
- **Lo que falta del contrato de captura es de NEXO (#297):** devolver y ordenar por `numeroLinea` en el GET, y que una línea de Cupo sin vehículo no se autocostee. Del lado de Cotifly quedó en #175 y #176.
- **En el portal de NEXO no existen** la bandeja de vehículos en cuarentena (S5-HU-19.3) ni editar el vehículo y poner el precio sin vehículo en la Mesa (S5-HU-19.4). Mientras no existan, `Nexo__EnviarCodigosCubicador` debe seguir apagada.
