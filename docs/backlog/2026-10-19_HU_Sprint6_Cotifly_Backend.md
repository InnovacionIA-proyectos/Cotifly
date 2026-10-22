# Sprint 6 — Cotifly backend

> **Estado:** borrador del Sprint 6; la fuente de verdad son las milestones de GitHub · **Dueño:** Duban Guerra (Backend) · **Fecha de redacción:** 2026-10-19

> **Fuente:** los pendientes de backend que quedaron abiertos al cerrar el Sprint 5, revisados contra `origin/QA` hasta #178 el 2026-10-19 (antes estaban en `docs/backlog/2026-10-16_PENDIENTES_DUBAN.md`). Salieron de construir el frontend contra el contrato real (2026-10-15 y 2026-10-16), de las pruebas en vivo con el cubicador y de la decisión de Sara sobre el rechazo de líneas en NEXO.
> **Convención:** mismo formato del backlog del Sprint 5 (`docs/backlog/2026-10-07_HU_Sprint5_Cotifly.md`). La numeración sigue desde HU-025 (HU-024 es la HU de datos para organizar los scripts de la base, de Sara). Todas las issues de este documento son **nuevas**: ninguna edita una issue que ya exista en GitHub.
> **Issues agrupadas al máximo** (2026-10-19, a pedido de Sara): cada milestone trae una sola issue de backend, dividida en partes A/B/C cuando cubre varios temas, para no fragmentar el trabajo de Duban.

---

## Notas de lectura

1. **Repo de todos los issues:** `Cotifly`. Roles: **Duban** = Backend, **Sara** = Datos y Frontend, **Kevin** = QA.
2. **Ya resuelto en el Sprint 5, no se repite aquí:**
   - CORS con `If-Match` e `Idempotency-Key`, y `ETag` expuesto (#178).
   - Listado regional del gerente, `GET /web/cotizaciones/regional` (#154, HU-016.1).
   - Campos de "Ver detalle" en `CotizacionResumen`: `deadline`, `score_prioridad` y `comercial` (#155, HU-018.1).
   - Header `X-Portal-clsUsuario-Id` centralizado (#175).
   - Motivo `BUSQUEDA_AGOTADA` (#176).
3. **Ya existe como HU, no se crea de nuevo:**
   - **HU-017.1** ("Margen promedio" contra la política). Sigue pendiente de la decisión de negocio entre la opción A (endpoint para el Comercial con los márgenes de política) y la opción B (cambiar `desviacion_promedio_margen`). Ver la sección "Relación con las HU que ya existen".
4. **Rama abierta a tener en cuenta:** `hu-023-defectos-e2e-1016` (17 commits del 2026-10-16 al 2026-10-19, todavía no está en QA) cambia el contrato de "Calcular cubicaje": pasa a ser asíncrono, con 202 y `GET /web/cubicacion/{id}`. Las issues de este sprint no dependen de ella, pero conviene fusionarla primero.

## Resumen

| HU | Issue | Título | Prioridad | Depende de |
|---|---|---|---|---|
| HU-010 | HU-010.5 | Filtro año/mes en el tablero del gerente | 🟠 Alta | — (parche listo) |
| HU-025 | HU-025.1 | Configuración por ambiente: banderas e identidad | 🟠 Alta / 🟡 Media | DBA, frontend (parte B) |
| HU-026 | HU-026.1 | Usar las columnas nuevas de la base de datos | 🟡 Media | — |
| HU-027 | HU-027.1 | Contrato web que necesita el frontend | 🟡 Media / ⚪ Baja | Decisión de negocio (parte D) |
| HU-028 | HU-028.1 | Rechazo de una línea con motivo (backend) | 🟡 Media | **Contrato de NEXO** |
| HU-028 | HU-028.2 | [BD] Columnas del rechazo | 🟡 Media | **Contrato de NEXO** |
| HU-028 | HU-028.3 | [QA] Rechazo de línea de punta a punta | 🟡 Media | HU-028.1, HU-028.2 |

**5 issues para Duban** (HU-010.5, HU-025.1, HU-026.1, HU-027.1, HU-028.1) + 1 para Sara (HU-028.2) + 1 para Kevin (HU-028.3).

---

## HU-010 — Rol Gerente Comercial y tablero de métricas (milestone existente)

> **Issue nueva** dentro de la milestone HU-010. HU-010.1 a 010.4 ya existen y no se modifican.

### [HU-010.5] [Backend] Filtro por año y mes en `GET /web/metricas/regional`
**Labels:** `backend`, `metricas`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-010 (milestone). La consume HU-010.3 (clientepro-frontend #51).**

El selector año/mes del tablero del gerente necesita filtrar por año o por mes completo, en vez de mandar `desde`/`hasta`.

El cambio ya está escrito y guardado como parche: **`docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`**, hecho sobre `QA` (`cf744ff`), y se comprobó que aplica limpio sobre `QA` con #175. Son 9 archivos: unas 45 líneas de código, 90 de pruebas y 50 de documentación (controlador, DTO, `clsTableroMetricasService.ResolverPeriodo`, tres suites de pruebas, `openapi.yaml`, `metricas_tablero.feature` y `07-api.md`).

- [ ] Revisar el cambio y confirmar que se sube pese al freeze.
- [ ] Rama desde `QA` y `git apply --3way docs/backlog/parches/2026-10-16_HU-010.5_metricas_anio_mes.patch`. Volver a comprobar contra `QA` actual (#178).
- [ ] Correr las pruebas unitarias, de API y de aceptación (`metricas_tablero.feature`).
- [ ] Revisar si hace falta anotar la extensión en ADR-016 §5.
- [ ] PR a `QA` (`Part of HU-010`) y avisarle al frontend (#51).

```gherkin
Escenario: Año completo
Dado un gerente comercial con regional
Cuando consulta /web/metricas/regional?anio=2025
Entonces el periodo aplicado va del 2025-01-01 al 2025-12-31

Escenario: Mes de un año
Cuando consulta /web/metricas/regional?anio=2025&mes=2
Entonces el periodo aplicado va del 2025-02-01 al 2025-02-28

Escenario: Parámetros inválidos
Cuando consulta con mes=5 sin anio, o con anio=2026&mes=13
Entonces responde 400 con un ErrorWeb

Escenario: Sin los parámetros nuevos
Cuando consulta sin anio ni mes
Entonces el comportamiento es el de antes
```

---

## HU-025 — Configuración por ambiente e identidad

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 6 · **Épica:** E4 — Despliegue, Configuración y Seguridad · **Talla:** S · **Roles:** Backend (Duban), Datos (Sara), Frontend (Sara)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo, **quiero** que cada ambiente tenga claras sus banderas de captura y de NEXO, y que Cotifly use la base de identidad correcta con los números de rol correctos, **para** que los comerciales entren con su rol y la captura se comporte igual en todos los ambientes.

### Contexto técnico
- `Captura:AceptarVehiculoCliente`, `Nexo:EnviarCodigosCubicador` y `Nexo:EnviarGrupoRutaId` vienen apagadas por defecto. En `origin/QA` solo están documentadas en `.env.example`; no están en ningún `appsettings`. Con `AceptarVehiculoCliente` apagada, el backend ignora el vehículo que digita el comercial, así que la pantalla de respaldo del frontend (HU-021-FE.2, #70) no tiene efecto.
- Cotifly pasó a usar como identidad `dbSolucionesTI_Prueba` (#172). Ahí los IDROLES son otros: según el script de #173, el 34 es `AdministracionZonasGeocerca` y el 32 es `ClienteProComercial`. La compartida sigue con `comercial = 34`, que era el valor correcto en la base vieja (`dbsolucionesti`).

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Banderas conocidas por ambiente
Dado los ambientes local, QA y producción
Cuando se revisa su configuración
Entonces cada bandera de captura y de NEXO tiene un valor decidido y documentado

Escenario: Un comercial real entra como comercial
Dado un usuario con el rol ClienteProComercial en la base de identidad del ambiente
Cuando inicia sesión en Cotifly
Entonces entra con el rol interno comercial
```

### Trazabilidad (event storming)
Fuente: revisión del contrato con el frontend (2026-10-15 y 2026-10-16), #172 y #173.

### Alcance en este repo (`Cotifly`)
1. Banderas e identidad (HU-025.1, partes A y B).

**HU también en:** `clientepro-frontend` (`cotifly-roles.ts`, parte B).

#### Issues en `Cotifly`

### [HU-025.1] [Config] Configuración por ambiente: banderas de captura/NEXO e identidad (rol `comercial` 34 → 32)
**Labels:** `backend`, `configuracion`, `datos`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend); el DBA corre los scripts de la parte B; Sara cambia el frontend
**Parte de HU-025 (milestone).**

**Parte A — Banderas de captura y de NEXO por ambiente.**
- [ ] Confirmar el valor actual de `Captura__AceptarVehiculoCliente`, `Nexo__EnviarCodigosCubicador` y `Nexo__EnviarGrupoRutaId` en cada ambiente desplegado (Dokploy).
- [ ] Decidir si `AceptarVehiculoCliente` se prende mientras el cubicador real no esté conectado en cada ambiente.
- [ ] `EnviarCodigosCubicador` sigue apagada hasta que el portal de NEXO tenga la bandeja de vehículos en cuarentena (S5-HU-19.3) y la edición del vehículo en la Mesa (S5-HU-19.4).
- [ ] Dejar los valores decididos en `DEPLOY.md` y en el `.env.example`.

**Parte B — Cambio de la base de identidad a `dbSolucionesTI_Prueba` y rol `comercial` de 34 a 32.** Son tres partes en **una sola ventana**:

| Parte | Qué cambia | Quién |
|---|---|---|
| Base de datos | `dbo_cotifly.Roles.idRolExterno` del rol `comercial`: de 34 a 32 (`db/scripts/operacion/corregir_rol_comercial_32.sql`) | DBA, con aprobación |
| Configuración | Cada ambiente apunta `ConnectionStrings__Identidad` a `dbSolucionesTI_Prueba` y, con la API detenida, el DBA corre `db/scripts/operacion/revincular_identidad_dbSolucionesTI_Prueba.sql` | Duban y el DBA |
| Frontend | `cotifly-roles.ts` reconoce como comercial el 34 (y el 25 en pruebas): pasar al 32 y revisar qué es el 25 en la base nueva | Sara |

- [ ] Confirmar con alguien que tenga acceso a `dbSolucionesTI_Prueba` que ahí el 32 es `ClienteProComercial` y el 31 es `ClienteProGerencial`. Hoy solo lo dice el script.
- [ ] Acordar la ventana con el DBA y con Sara.
- [ ] Cambiar `ConnectionStrings__Identidad` en cada ambiente y correr los dos scripts de operación en esa misma ventana.
- [ ] **Cuidado con el orden:** en un ambiente que siga con la identidad vieja, el 32 es *proveeduría*.
- [ ] Probar el login de un comercial real y del gerente (el 31 no cambia).

---

## HU-026 — Usar las columnas nuevas de la base de datos

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 6 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** S · **Roles:** Backend (Duban), Datos (Sara)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo, **quiero** que el backend use las columnas que ya existen en la base compartida, **para** dejar de depender de desfases de fecha y de búsquedas por nombre, y no perder datos al reiniciar la API.

### Contexto técnico
- Script `03` (en la compartida desde el 2026-10-14): `CotizacionVehiculosLineaSolicitud.ordenVehiculo` y `CotizacionServiciosConexos.idServicioConexoCatalogo`. Hoy el orden del camión sale de un desfase de 1 ms en `createdAt` (`clsVehiculoLineaSolicitud.OrdenCreacion`) y el conexo se resuelve por nombre (`clsResolutorConexosCatalogo`). Las filas nuevas quedan con esas columnas en NULL.
- Script `05` (en la compartida desde el 2026-10-16): `CotizacionServiciosConexos.categoriaServicioConexo`. Hoy la categoría se lee del catálogo de NEXO cada vez que se arma el costeo (caché de 30 min).
- Script `06` (propuesto, **no aplicado**): tabla `CapturasIdempotentes`. Hoy la idempotencia de la captura vive en memoria (`clsRegistroCapturasIdempotentesEnMemoria`) y se pierde al reiniciar o con dos réplicas.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Orden de los camiones estable
Dado una ruta con tres camiones creados en la misma petición
Cuando se lee la ruta
Entonces los camiones salen en el orden de ordenVehiculo, sin depender de createdAt

Escenario: Idempotencia sobrevive a un reinicio
Dado una captura con Idempotency-Key ya confirmada
Cuando la API se reinicia y llega la misma petición
Entonces responde la misma cotización y no crea otra solicitud en NEXO
```

### Trazabilidad (event storming)
Fuente: encabezados de los scripts `03`, `05` y `06` (#158 y #173); aplicación del `03` y el `05` en la compartida.

### Alcance en este repo (`Cotifly`)
1. Las cuatro columnas nuevas (HU-026.1, partes A, B y C).

**HU también en:** ninguna.

#### Issues en `Cotifly`

### [HU-026.1] [Backend] Mapear las columnas nuevas de la base de datos
**Labels:** `backend`, `deuda-tecnica`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend); Sara aplica el `06` en la compartida
**Parte de HU-026 (milestone).**

**Parte A — Orden del camión e id del catálogo del conexo (script `03`).**
- [ ] Mapear `ordenVehiculo` en EF y escribirlo al crear o replanificar camiones (1, 2, 3 por ruta). Retirar el desfase de 1 ms en `createdAt`.
- [ ] Mapear `idServicioConexoCatalogo` y llenarlo al capturar, agregar o importar conexos. Dejar de resolver el conexo por nombre cuando la columna tenga valor.
- [ ] Agregar las dos columnas al chequeo de `/health/ready`.

**Parte B — Categoría del conexo (script `05`).**
- [ ] Guardar `categoriaServicioConexo` al capturar e importar los conexos, como propone el encabezado del `05`. Las filas viejas siguen leyendo la categoría del catálogo.

**Parte C — Idempotencia persistente (script `06`).**
- [x] Pasar la idempotencia de la captura de memoria a `CapturasIdempotentes`: la clave se reserva en la misma transacción que confirma el folio. **Tabla ya creada en la compartida (2026-10-22, script `06`, probado en Docker).** Falta que el backend la use.
- [ ] **Antes de fusionar:** avisarle a Sara para que apruebe y aplique el `06` en la compartida.

**Pruebas (de todas las partes):** orden estable con camiones creados en el mismo milisegundo; conexo agregado e importado con su id del catálogo y su categoría; captura repetida después de reiniciar la API no crea otra solicitud en NEXO.

---

## HU-027 — Contrato web que necesita el frontend

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 6 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** M · **Roles:** Backend (Duban), Frontend (Sara)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** comercial, **quiero** poder recargar el detalle de una cotización, crear una versión nueva cambiando la carga de cualquier ruta y escoger la clase de contenedor del catálogo real, **para** trabajar en Cotifly sin perder datos ni depender de listas fijas.

### Contexto técnico
Huecos encontrados al construir el frontend contra el contrato real (`openapi.yaml`, 2026-10-15 y 2026-10-16). No son errores: son funciones que el contrato no tiene. Si no se hacen, hay que recortar el alcance de esas HU de frontend.
- **Detalle:** no existe `GET /web/cotizaciones/{folio}`. El frontend combina `GET /web/mis-cotizaciones` (cliente, NIT, `estado_envio`, `ganada`, `deadline`, `comercial`) con `GET .../costeo` (rutas, camiones, mercancías, conexos, margen y precio). Al recargar la página no tiene de dónde sacar la cabecera.
- **Nueva versión:** `RutaNuevaVersion` solo trae línea, origen, destino y margen; no trae mercancías, conexos ni condiciones. `DatosCargaWeb` es una sola carga sin ruta, y con más de una ruta `POST .../costeo/re-solicitar` responde 409 `ESTADO_NO_PERMITE` (`VARIAS_RUTAS`). Hay un trabajo pausado por el freeze ("DatosNuevaVersion ampliado", HU-004.4) en el stash de Sara.
- **Clase de contenedor:** el backend ya lee `GET /api/catalogos/clases-contenedor` de NEXO, pero solo para validar. `CatalogosFlete` trae `tipos_vehiculo`, `tipos_carroceria` y `clases_viaje`, sin `clases_contenedor`. Hoy no hay un error activo (las descripciones de NEXO coinciden con la lista fija del frontend), pero hay riesgo de que se desincronicen. Además, `openapi.yaml` (`SolicitudCosteoWeb.clase_contenedor`) todavía menciona los 5 códigos fijos que HU-023.1 retiró.
- **Correo:** el asunto por defecto es `Cotización {folio} — Botero Soto` y el cuerpo es fijo; el precio solo va en el PDF.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Recargar el detalle
Dado una cotización del comercial
Cuando el frontend pide su detalle por folio
Entonces recibe en una sola respuesta la cabecera, las rutas, los camiones, las mercancías, los conexos, el margen y el precio

Escenario: Versión nueva cambiando una ruta de varias
Dado una cotización con tres rutas
Cuando el comercial cambia las mercancías de la ruta 2 y crea la versión nueva
Entonces se crea -vN, solo se replanifican los camiones de la ruta 2 y las otras rutas se copian igual

Escenario: Clase de contenedor del catálogo
Cuando el frontend pide GET /web/catalogos-flete
Entonces recibe clases_contenedor con las descripciones de NEXO
```

### Trazabilidad (event storming)
Fuente: preguntas del frontend sobre HU-021-FE.7 (#75), HU-021-FE.8 (#76) y HU-021-FE.11 (#79), respondidas contra `openapi.yaml` y el código de QA.

### Alcance en este repo (`Cotifly`)
1. Las cuatro partes del contrato (HU-027.1, partes A a D).

**HU también en:** `clientepro-frontend` (HU-021-FE.7, FE.8 y FE.11 consumen estos cambios).

#### Issues en `Cotifly`

### [HU-027.1] [Backend] Completar el contrato web que necesita el frontend
**Labels:** `backend`, `contrato`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-027 (milestone). La consumen HU-021-FE.7, FE.8 y FE.11 (#75, #76, #79).**

**Parte A — `GET /web/cotizaciones/{folio}`: detalle completo de una cotización.**
- [ ] Endpoint nuevo que devuelva la cabecera de `CotizacionResumen` junto con el costeo (`CosteoCotizacion`), en una sola llamada. Solo el dueño; 404 si no es suya.
- [ ] Incluir `row_version` (y el `ETag`) para las acciones que siguen.
- [ ] Contrato en `openapi.yaml` y en `docs/docs_front/CONTRATO_API_WEB.md`.

**Parte B — Nueva versión por ruta: datos completos y cambios con varias rutas.**
- [ ] `GET /web/cotizaciones/{id}/nueva-version`: agregar a cada `RutaNuevaVersion` sus mercancías, sus conexos y sus condiciones (contenedor, horas, valor de la mercancía, peso). Revisar el WIP pausado "DatosNuevaVersion ampliado" (HU-004.4) que Sara tiene en su stash.
- [ ] `POST .../costeo/re-solicitar`: aceptar `datos_carga` por ruta (con `linea_orden` o `linea_solicitud_id`) para cotizaciones de varias rutas. Solo se replanifican los camiones de las rutas que cambian; las demás se copian igual. Mantener la forma actual para una ruta.

**Parte C — `clases_contenedor` en `GET /web/catalogos-flete`.** Era la parte B de HU-023.1 en el plan del Sprint 5 y quedó sin hacer.
- [ ] Agregar `clases_contenedor` a `CatalogosFlete` (`OpcionCatalogo`: `valor` = `etiqueta` = `descripcion` de NEXO, como `clases_viaje`), con la misma caché y el mismo comportamiento si NEXO no responde.
- [ ] Corregir `openapi.yaml` (`SolicitudCosteoWeb.clase_contenedor` y `RutaCapturaWeb`): quitar los 5 códigos fijos que HU-023.1 retiró.
- [ ] Avisarle al frontend para que retire su lista fija.

**Parte D — Total de la solicitud en el correo. Pendiente de decisión de negocio antes de construir.**
- [ ] Confirmar con negocio si el correo debe mostrar el total de la solicitud en el asunto, en el cuerpo o en ninguno.
- [ ] Si se aprueba: incluirlo en `clsPlantillaCorreo` y en `POST .../correo/vista-previa`, en la moneda de la cotización.

**Pruebas (de todas las partes):** detalle con dueño y con ajeno (404); versión nueva cambiando una ruta de tres; ruta sin cambios conserva sus camiones y conexos; catálogo de clases de contenedor con las descripciones reales.

---

## HU-028 — Rechazo de una línea con motivo en la Mesa de Costeo de NEXO

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 6 · **Épica:** E3 — Integración Cotifly ↔ Nexo · **Talla:** L · **Roles:** Backend (Duban), Datos (Sara), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly`
> ⚠ **Bloqueada por NEXO:** el contrato todavía no está cerrado. Las decisiones de negocio ya están tomadas (2026-10-16). **No construir hasta que NEXO publique el contrato.**
> **Issues ya creadas en GitHub:** #182 (HU-028.2), #183 (HU-028.1), #192 (HU-028.3). Fuente cruzada: `2026-10-16_ISSUES-POR-REPOSITORIO.md` del chat de NEXO (ahí esta HU-028.2 era "DATA-4").

### Historia
**Como** comercial, **quiero** que una línea que la Mesa de Costeo de NEXO rechaza no frene mi cotización y que yo vea por qué se rechazó, **para** poder entregar el resto de la cotización al cliente.

### Contexto técnico
**Decisión de Sara (2026-10-16).** En la Mesa de Costeo de NEXO, el Administrador de Tarifas puede rechazar UNA línea con un motivo obligatorio:
- **No frena la cotización:** se puede entregar, y salen el PDF y el Excel.
- **La línea rechazada no suma al total y no lleva precio.**
- **El motivo lo ve solo el comercial** (pantalla de costeo y Mesa). El PDF y el Excel muestran la línea como **"Sin costeo"**, sin precio y **sin** el motivo.
- **Deshacer el rechazo** solo se puede antes de entregar.
- **Si se rechazan todas las líneas,** la cotización se entrega igual, con el motivo de cada una.

**Cómo está hoy Cotifly (verificado en el código):**

| Situación | Qué hace hoy | Problema con el rechazo |
|---|---|---|
| Línea **borrada** en NEXO (#134) | El camión queda `descartada` (`clsVehiculoLineaSolicitud.DescartarPorLineaBorrada`): pierde sus costos, no cuenta en los totales (`VehiculosQueCuentan`) y no recibe conexos | No guarda motivo; si todos los camiones de la ruta están descartados, la ruta queda sin flete |
| Línea **sin flete** en una cotización **entregada** | `clsDerivacionEstadoCosteo.DerivarLinea` la deja `entregada` sin costo | La ruta queda sin precio, el total en `null` y el PDF responde 409 `COSTEO_NO_LISTO` |
| Línea sin flete **antes de entregar** | `Derivar` solo da `autocosteo` si todas las líneas tienen flete | Una línea rechazada dejaría la cotización en `pendiente_costeo` para siempre |

Hoy Cotifly no puede distinguir una línea rechazada de una pendiente ni de una borrada.

**Lo que se le pide a NEXO** en `GET /api/solicitudes/{id}`, por línea (y si aplica en `GET /api/cotizaciones/{id}`):

| Campo | Tipo | Para qué |
|---|---|---|
| `estadoLinea` (o un valor nuevo en el estado de la línea) | enum, con `Rechazada` | Distinguir rechazada de pendiente y de borrada |
| `motivoRechazo` | texto, obligatorio si está rechazada | Mostrárselo al comercial |
| `rechazadaAt` y usuario (opcional) | fecha y usuario | Auditoría |

**Falta acordar con NEXO:** si `CotizacionDto` omite la línea rechazada o la trae sin flete con su estado, y cómo se ve "se rechazaron todas".

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Una ruta rechazada no frena la cotización
Dado una cotización de tres rutas donde la Mesa rechazó la ruta 2 con motivo
Cuando NEXO entrega la cotización
Entonces el total es la suma de las rutas 1 y 3
Y el PDF muestra la ruta 2 como "Sin costeo", sin precio y sin motivo
Y el comercial ve el motivo en la pantalla de costeo

Escenario: Deshacer el rechazo antes de entregar
Dado una línea rechazada en una cotización que sigue en la Mesa
Cuando NEXO la des-rechaza
Entonces Cotifly la vuelve a mostrar como pendiente de costeo y borra el motivo

Escenario: Todas las rutas rechazadas
Dado una cotización con todas sus rutas rechazadas
Cuando NEXO la entrega
Entonces la cotización queda entregada, sin total, con el motivo de cada ruta visible
```

### Trazabilidad (event storming)
Fuente: decisión de Sara del 2026-10-16 (con el chat de NEXO) y revisión del código de QA (#177).

### Alcance en este repo (`Cotifly`)
1. Backend: lectura del rechazo y todo lo que lo expone (HU-028.1, partes A y B).
2. [BD] Columnas del rechazo (HU-028.2).
3. [QA] Prueba de punta a punta (HU-028.3).

**HU también en:** `NEXO` (rechazo en la Mesa de Costeo), `clientepro-frontend` (mostrar el estado y el motivo).

#### Issues en `Cotifly`

### [HU-028.1] [Backend] Rechazo de una línea: leerlo de NEXO y reflejarlo en todo Cotifly
**Ya creada en GitHub: #183.**
**Labels:** `backend`, `integracion`, `nexo`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-028 (milestone). Depende del contrato de NEXO y de HU-028.2 (BD).**

**Parte A — Lectura del costeo (polling) y estados.**
- [ ] Leer el estado y el motivo de cada línea de la foto de NEXO (`clsCosteoConsultadoNexo`, `clsLineaCosteoNexo` o las líneas de la solicitud).
- [ ] Estado nuevo del camión `rechazada`, distinto de `descartada` (que es "línea borrada", #134), con `motivoRechazo`.
- [ ] Deshacer el rechazo: mientras la cotización no esté entregada, un camión `rechazada` no es terminal. Si NEXO lo des-rechaza, vuelve a `pendiente_costeo` y se borra el motivo. Entregada, el rechazo es definitivo.
- [ ] `clsDerivacionEstadoCosteo.Derivar`: `autocosteo` cuando todas las líneas **no rechazadas** tienen flete.
- [ ] `clsResumenEstadoCosteo` y `VehiculosQueCuentan`: ignorar las `rechazada`, igual que las `descartada`. Una ruta con todos sus camiones rechazados queda `rechazada`, sin precio, y no bloquea la cotización.
- [ ] Emparejamiento (#134): una línea rechazada sigue existiendo en NEXO, no es "línea sin dueño".

**Parte B — Todo lo que expone el rechazo: totales, PDF, Excel, contrato web, versiones y resultado.**
- [ ] `precio_final` = suma de las rutas **no rechazadas**. Ruta rechazada: `precio_final_linea = null`; ajustar el margen o agregar conexos en ella → 409 con un código claro (por ejemplo `RUTA_RECHAZADA`).
- [ ] Ruta con algunos camiones rechazados: precio de los vigentes; los conexos se pegan al primer camión **no rechazado** (`clsServiciosConexosCosteoService`).
- [ ] PDF (`POST .../costeo/pdf`): las rutas rechazadas no cuentan en la regla "todas con precio"; se muestran "Sin costeo", sin precio y sin motivo, en el resumen del pliego y en su sección.
- [ ] **Por definir con Sara:** con todas las rutas rechazadas, ¿se permite el PDF o se bloquea con un 409 propio (propuesta: bloquearlo)?
- [ ] Excel de la licitación (`GET /web/licitaciones/{folio}/excel`): la fila rechazada sale "SIN COSTEO", sin precio y sin motivo; no cuenta en el total de "Resumen Cotifly"; igual en "Camiones Cotifly".
- [ ] Métricas (ADR-016): el valor cotizado excluye las rutas rechazadas.
- [ ] `GET .../costeo`: exponer en `RutaCosteo` y `CamionCosteo` el estado `rechazada` y `motivo_rechazo` (cambio de enum: avisarle al frontend).
- [ ] `CotizacionResumen` ("Mis cotizaciones" y el listado regional): el resumen del estado ignora las rechazadas; el total sale sin ellas.
- [ ] `Licitacion.lineas[]` (pantalla): estado "Rechazada en NEXO" con su motivo; las rechazadas no son pendientes.
- [ ] Todas las rutas rechazadas: la cotización queda `entregada`, sin total y con el motivo de cada ruta visible; el polling se detiene.
- [ ] **Por decidir:** ¿la `-vN` copia la ruta rechazada (y NEXO la vuelve a evaluar) o la omite? Propuesta: copiarla, avisarle al comercial y dejarle quitarla. En el mismo folio, la re-solicitud vuelve a capturar la línea y el rechazo anterior queda en la auditoría.
- [ ] Marcar ganada con rutas rechazadas debe funcionar.

### [HU-028.2] [BD] Columnas del rechazo
**Ya creada en GitHub: #182** (abierta por @jpvelez-bts).

> **Actualización 2026-10-22:** el contrato de NEXO ya se verificó (chat de NEXO, contra `origin/QA` y `origin/DEPLOY` de NEXO):
> - `estadoLinea = "rechazada"` y `motivoRechazo` **ya están en QA/main de NEXO** (S6-HU-24.1/#309, S6-HU-24.3/#320), listos para leer en cada reconsulta del polling.
> - **Es por línea/camión individual**, confirmado en el comando de NEXO (`LineaSolicitudId` único por llamada, sin `grupoRutaId`): la columna por camión está bien pensada.
> - **`RechazadaPor`/`RechazadaAt` NO se pueden leer en ninguna reconsulta** hoy: solo salen en la respuesta puntual del endpoint de rechazar, que además solo existe en DEPLOY. Por eso el script **no agrega `rechazadaAt`** por ahora.
>
> **Script escrito y probado en Docker:** `db/scripts/pendientes/10_rechazo_linea_camion.sql`. Agrega `rechazada` al CHECK de `estadoCosteo` del camión y de la ruta, y `motivoRechazo NVARCHAR(500) NULL` en el camión. Probado: datos previos intactos, idempotente, rechaza estados inventados, compatible con el `03`. **No aplicado en la compartida todavía** (falta el mapeo de backend, HU-028.1/#183, antes de que tenga sentido llenarlo).
**Labels:** `datos`, `base-de-datos`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos)
**Parte de HU-028 (milestone). Depende del contrato de NEXO. Bloquea a HU-028.1.**

- [ ] Script en `db/scripts/`: `CotizacionVehiculosLineaSolicitud` con `rechazada` en el CHECK de `estadoCosteo`, `motivoRechazo NVARCHAR(…) NULL` y, opcional, `rechazadaAt`.
- [ ] Revisar los CHECK de `estadoCosteo` en `CotizacionLineasSolicitud` y `CotizacionesSolicitud` si la ruta también puede quedar `rechazada`.
- [ ] Probarlo en Docker (idempotente, en una transacción) y aplicarlo en la compartida.

### [HU-028.3] [QA] Rechazo de línea de punta a punta
**Ya creada en GitHub: #192.**
**Labels:** `qa`, `sprint-6`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Kevin (QA)
**Parte de HU-028 (milestone). Depende de HU-028.1 y HU-028.2.**

- [ ] Una ruta rechazada de tres: total, PDF y Excel sin la ruta, y el motivo visible solo en la pantalla del comercial.
- [ ] Rechazar y deshacer antes de entregar.
- [ ] Todas las rutas rechazadas.
- [ ] Licitación con una fila rechazada (Excel exportado).
- [ ] Versión nueva de una cotización con una ruta rechazada.

---

## Relación con las HU que ya existen

| HU existente | Qué pasa | Dónde se trabaja |
|---|---|---|
| HU-017.1 ("Margen promedio" contra la política) | Sigue pendiente de la decisión de negocio (opción A u opción B). El margen es por ruta, y no está decidido si el promedio es simple o ponderado por costo | En la misma HU-017.1, cuando negocio decida |
| HU-021-FE.7, FE.8 y FE.11 (`clientepro-frontend`) | Consumen HU-027 | Frontend, después de HU-027 |
| HU-024 (scripts de la base, Sara) | El script de HU-028.2 y el `06` de HU-026.1 entran a `db/scripts/` con esa organización | HU-024 |

## Para saber, sin acción

- **El `07` (`BUSQUEDA_AGOTADA`) es de Duban** (`db/scripts/07_motivo_busqueda_agotada.sql`, #176). Sara lo aplica en la compartida.
- **Lo que falta del contrato de captura es de NEXO (#297):** devolver y ordenar por `numeroLinea` en el GET, y que una línea de Cupo sin vehículo no se autocostee.
- **En el portal de NEXO no existen** la bandeja de vehículos en cuarentena (S5-HU-19.3) ni editar el vehículo y poner el precio sin vehículo en la Mesa (S5-HU-19.4).
- **Peso con tara:** NEXO planea que el "peso total" de la línea incluya la tara. El `peso_kg` que manda Cotifly es solo la carga. Falta decidir quién suma la tara antes de que NEXO cambie su validación.
  - **Detalle nuevo (2026-10-22, vía `clientepro-frontend`):** la comercial aclaró que, a su juicio, `pesoKg` **siempre** debería incluir la tara, incluso en rutas CON mercancías itemizadas — no solo en Contenedor/Cupo/Devolución Contenedor (donde hoy ya funciona así, sin problema: `clsSolicitudCosteo.cs:194` no compara contra nada si no hay posiciones con peso).
  - **Choca con el código de hoy:** `clsSolicitudCosteo.cs:201-204` (`PESO_INCONSISTENTE`) exige que `pesoKg` cuadre dentro del 1% contra `sum(cantidad × peso_kg)` de las mercancías (que es neto, sin tara). Si `pesoKg` siempre trae tara, esa validación fallaría en casi cualquier ruta con mercancías reales (la tara no es despreciable).
  - **No construir nada en el frontend** que asuma "siempre con tara" hasta que esto se decida — rompería la captura de rutas con mercancías tal como está el código. Si se decide que sí, el ajuste de backend es mediano: tocar la regla de `PESO_INCONSISTENTE` (quitar la comparación, o restar una tara conocida antes de comparar) y probablemente el contrato con NEXO.
