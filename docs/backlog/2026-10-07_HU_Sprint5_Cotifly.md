# Sprint 5 — Cotifly (HUs de backend y frontend)

> **Fecha de redacción:** 2026-10-07. Documento vivo: se va completando a medida que se van confirmando más hallazgos/pedidos para este sprint — no es la versión final todavía.
> **Fuente:** `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md` (hallazgos encontrados probando en vivo contra `tbsdatacenter`/`dbTarifario_Nexo_Pruebas`, 2026-09-29 a 2026-10-05) + deuda técnica detectada aparte.
> **Convención:** sigue el mismo formato de `docs/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md` (Historia / Contexto / checklist con `[ ]`), numeración continúa desde HU-013 (la de `ADR-018`, ya construida) — estas son todas nuevas, HU-014 en adelante.

---

## Revisión del 2026-10-08: qué aplica después de la reestructuración de la base de datos

**El Sprint 5 va DESPUÉS de `docs/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md` (HU-021, "[Corrección con BD]").** El 2026-10-08 se recreó `dbo_cotifly` con el esquema nuevo (solicitud → rutas → camiones) y el backend actual no funciona contra él hasta terminar esa HU. Se revisó cada HU de abajo contra ese esquema:

| HU | Estado | Qué cambia | Depende de |
|---|---|---|---|
| 014.1 `estadoEnvio` sin `CHECK` | **Aplica, se absorbe en HU-021** | El `CHECK` entra al esquema (HU-021.2) y la tolerancia de filas malas se hace al reescribir la consulta (HU-021.4) | HU-021.2, 021.4 |
| 014.2 Cotización de prueba de Duban | **Ya no aplica** | La base compartida se borró y recreó; la pregunta perdió sentido. Pasa a "Descartado" | — |
| 014.3 Commitear el header `X-Portal-clsUsuario-Id` | **Aplica** | La lista creció a 6 archivos (se sumó el análisis de Excel) y siguen sin commit | — |
| 014.4 Retirar el borrador de métricas | **Aplica, coordinar** | Las vistas ya se adaptaron al esquema nuevo; si esto se hace primero, se quitan en vez de adaptarlas | HU-010, HU-021.10 |
| 015.1 Catálogo de tipo de vehículo / clase de viaje | **Aplica con cambios** | El comercial ya no escoge vehículo ni carrocería: salen del cubicador. Sigue haciendo falta la clase de viaje | HU-021.5, 021.6 |
| 015.2 Clase de contenedor | **Aplica** | Sin cambios: `claseContenedor` sigue en la ruta | HU-021.4 |
| 015.3 Servicios conexos | **Aplica con cambios** | Los conexos se guardan por ruta en una tabla (HU-021.8). Aquí queda enviar la selección a Nexo | HU-021.8 |
| 016.1 Listado regional | **Aplica** | El listado se construye sobre la estructura nueva | HU-021.4, 021.9 |
| 017.1 Margen promedio | **Aplica con cambios** | El margen ahora es por ruta; el promedio se calcula sobre rutas | HU-021.8, 021.10 |
| 018.1 Campos en "Ver detalle" | **Aplica, reformulada** | Origen, destino, peso y valor ya no están en la cabecera: son de cada ruta | HU-021.9, 021.11 |
| 018.2 Catálogo de referencia | **Aplica** | "Nivel línea" pasa a ruta y camión | HU-021.11 |
| 019.1 Ganada/perdida motivo + comentario | **Aplica, corregida** | Revisar a qué endpoint llama el frontend (ver la nota en la HU) | HU-021.13 |
| 020.1 Exportar Excel | **Aplica** | El contenido por ruta y camión | HU-021.9 |

---

## HU-014 — Deuda técnica y correcciones de backend

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)

### [HU-014.1] `estadoEnvio` sin `CHECK` en BD: un solo valor inválido tumba TODO "Mis cotizaciones" del usuario

**Labels:** `backend`, `bug`
**Síntoma:** `GET /web/mis-cotizaciones` respondió `500` para un usuario con una sola cotización con un valor fuera de catálogo (`estadoEnvio = 'pendiente'`, insertado por error en datos de prueba).

- [ ] **Causa:** `clsCotizacionRepository.ListarConLineaPorUsuarioAsync` (`src/Cotifly.Infraestructura/Cotizaciones/Repositories/clsCotizacionRepository.cs:109`) trae TODAS las cotizaciones del usuario en una sola consulta materializada. El mapeo de EF Core (`clsConvertidorCodigoEnum<TEnum>`) lanza `ArgumentException` sin capturar si el código no está en el enum (`EstadoEnvio`/`EstadoCosteo`) — la excepción no se maneja en ningún punto de la cadena, así que se propaga como `500` y bloquea la consulta completa para ese usuario, no solo la fila mala.
- [ ] **Por qué importa:** `estadoCosteo` ya tiene `CHECK` en BD desde el 0004 (`ck_cotizaciones_estadoCosteo`), pero `estadoEnvio` sigue sin ninguno — nada impide hoy que una fila quede fuera de `sin_enviar`/`enviada`/`reenviada` y tumbe el módulo para ese comercial.
- [ ] Corto plazo: agregar un `CHECK` para `estadoEnvio` igual al patrón de `ck_cotizaciones_estadoCosteo`. **Revisión 2026-10-08:** el esquema nuevo (`db/scripts/01_crear_esquema.sql`) tampoco lo trae. Se hace dentro de **HU-021.2**, en el script de instalación y en el `0008`; en la base compartida ya recreada, con un `ALTER` puntual.
- [ ] Mediano plazo: que `ListarConLineaPorUsuarioAsync` (o el converter) no deje que una fila con un código inesperado tumbe las demás — tolerar y loguear en vez de lanzar, o mapear fila por fila en vez de una proyección que materializa todo de una. **Revisión 2026-10-08:** esa consulta se reescribe en **HU-021.4**; hacer esto ahí.

### [HU-014.2] Confirmar contra qué base insertó Duban su cotización de prueba

**Labels:** `backend`, `investigacion`
> **Ya no aplica (2026-10-08).** La base compartida se borró y recreó con el esquema nuevo, y esa cotización ya no existe en ningún lado. Las pruebas de ahora en adelante se hacen con datos nuevos creados por el flujo nuevo. Se deja el texto original por trazabilidad.

- ~~La cotización de prueba que Duban insertó manualmente para probar "Mis cotizaciones" no apareció en `dbo_cotifly.Cotizaciones` de `tbsdatacenter`/`dbTarifario_Nexo_Pruebas` (la base real compartida) — confirmar contra qué servidor/base la insertó, para descartar una segunda base "de pruebas" desincronizada dando vueltas.~~

### [HU-014.3] Commitear como definitivo el ajuste del header `X-Portal-clsUsuario-Id`

**Labels:** `backend`, `tarea-tecnica`
**Decisión (2026-10-05):** Nexo no va a corregir el nombre del header de su lado (bug admitido en `CONTRATO.md §7`, discrepancia #1) — el ajuste queda **permanente** en Cotifly, no es un parche a revertir.

- [ ] El ajuste (`"X-Portal-Usuario-Id"` → `"X-Portal-clsUsuario-Id"`) ya está aplicado en el código de los adaptadores de Nexo y los comentarios ya no dicen "revertir cuando lo arreglen". **Son 6 archivos** (a 2026-10-08): `clsBuscadorClientesNexo.cs`, `clsCatalogosMaestrosNexo.cs` (antes `clsCatalogosFleteNexo.cs`), `clsCosteoNexo.cs` (dos sitios), `clsCubicacionNexo.cs`, `clsMunicipiosNexo.cs` y `clsAnalizadorExcelNexo.cs`.
- [ ] **Falta:** commitear estos cambios como definitivos — hoy siguen sin commitear en la rama de trabajo (se dejaron fuera del commit de la reestructuración a propósito, para que los commitee Duban). Ojo con `clsCosteoNexo.cs`: HU-021.7 lo reescribe; conviene commitear esto **antes** para no mezclar los dos cambios.
- [ ] Cuando el cubicador entre (HU-021.5), su adaptador **no** usa este header: autentica con `Authorization: Bearer <CUBICADOR_API_KEY>`.

### [HU-014.4] Retirar el borrador obsoleto del tablero de métricas (`/actividad`, `/ajustes-margen`)

**Labels:** `backend`, `tarea-tecnica`, `deuda-tecnica`
**Depende de que HU-010 (tablero real, `/web/metricas/comercial` y `/regional`) esté validado en producción sin depender de esto.**

`GET /web/metricas/actividad` y `GET /web/metricas/ajustes-margen` fueron el primer borrador del tablero, antes del diseño final de HU-010.1. Ya están marcados `(obsoleto)` en el propio código (`clsMetricasController.cs`) y `clientepro-frontend` no los consume en ningún lado (confirmado por búsqueda en el repo).

- [ ] Confirmar que ningún consumidor externo (Postman guardado, script, otro servicio) sigue llamando estos dos endpoints.
- [ ] Retirar los endpoints `Actividad`/`AjustesMargen` de `clsMetricasController.cs`.
- [ ] Retirar `ListarActividadAsync`/`ListarAjustesMargenAsync` de `IClsMetricasRepository`/`clsMetricasRepository.cs`, y los DTOs `clsActividadComercial`/`clsAjustesMargenComercial`.
- [ ] Retirar las vistas `vActividadPorComercial` (creada en 0001, redefinida en 0003) y `vAjustesMargenPorComercial` (creada en 0006) con un script `000N_retirar_vistas_actividad.sql` — no destructivo sobre datos (son vistas). **Revisión 2026-10-08:** las dos vistas ya se redefinieron en `0008` y en `01_crear_esquema.sql` para el esquema nuevo (`vAjustesMargenPorComercial` saca la línea de negocio de la ruta del ajuste). Si esta HU se hace **antes** de HU-021.10, se quitan de ambos scripts en vez de adaptarlas; si se hace después, el script de retiro cubre también la instalación desde cero.
- [ ] Actualizar `docs/docs_front/CONTRATO_API_WEB.md`/`specs/openapi.yaml` quitando ambas rutas.

---

## HU-015 — Catálogos y servicios conexos: usar las fuentes correctas de Nexo

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)
> **Contexto general:** revisando qué le falta a Cotifly para que toda la información del formulario de "Nueva cotización" se cree correctamente en las tablas de Nexo (no solo se calcule/muestre localmente), se comparó `CONTRATO.md` de Nexo (incluido lo nuevo bajo `/api/catalogos/*`) contra lo que Cotifly implementaba hasta el 2026-10-05.

### [HU-015.1] Migrar tipo de vehículo / clase de viaje al catálogo maestro de Nexo

**Labels:** `backend`
- [ ] Cotifly los traía de `GET /api/biblioteca-sap-historico/filtros` (pensado para filtrar histórico de tarifas, no un catálogo maestro) en vez de `GET /api/catalogos/tipos-vehiculo` y `GET /api/catalogos/clases-viaje`.
- [ ] **Nota 2026-10-07:** el pull de QA de esa fecha ya trajo la corrección (`clsCatalogosMaestrosNexo.cs`/`clsCatalogosFleteNexo.cs` reescritos, HU-013.10 #106) — **verificar si esta issue ya quedó resuelta** antes de abrirla, puede que solo falte confirmar en QA.
- [ ] Confirmar con Duban si existe un catálogo maestro equivalente para **carrocería**, o si ese campo se queda en `biblioteca-sap-historico/filtros` por ahora.
- [ ] **Revisión 2026-10-08 — cambia el alcance.** Con el cubicador (HU-021.5) el comercial **ya no escoge** tipo de vehículo ni carrocería: salen de la recomendación y se guardan por camión. Entonces este catálogo solo sigue haciendo falta para la **clase de viaje** (que sí es de la ruta) y, si se deja, para que el admin de fletes corrija un camión que quedó sin vehículo. Reformular esta issue a "clase de viaje contra `GET /api/catalogos/clases-viaje`" y dejar vehículo y carrocería para el acuerdo de códigos entre Nexo y el cubicador (en Nexo el vehículo es una sola combinación del catálogo: tipo + carrocería + rango de peso, `tbl_configuraciones_vehiculo`).

### [HU-015.2] Conectar clase de contenedor al catálogo real de Nexo

**Labels:** `backend`
- [ ] Cotifly usa un catálogo **hardcodeado propio** (`ClaseContenedor.cs` en el backend, `clase-contenedor.catalogo.ts` en el frontend) — nunca llama a `GET /api/catalogos/clases-contenedor`. Si Nexo agrega/renombra/retira un valor, Cotifly queda desincronizado sin enterarse.

### [HU-015.3] Servicios conexos: catálogo disponible + enviar la selección del comercial a Nexo

**Labels:** `backend`
**La más importante de este grupo.** `clsCosteoNexo.cs` solo **lee** los conexos que ya traiga la cotización (`LeerCotizacion`, a partir de `POST /api/cotizaciones`), pero Cotifly no tiene forma de:

- [ ] Traer el catálogo de servicios conexos disponibles — `GET /api/servicios-conexos` (paginado, `Roles: Todos`).
- [ ] Enviar la selección del comercial de vuelta a Nexo — `POST /api/cotizaciones/{id}/conexos` (`Roles: Comercial, CoordinadorFletes, Admin`; body `{ items: [{ servicioConexoId, cantidad?, valorAplicado? }] }`).
- [ ] Sin esto, la elección del comercial se calcula/guarda solo localmente — la cotización real en las tablas de Nexo queda sin esos conexos (afecta reportes/biblioteca de tarifas ganadas/auditoría del lado de Nexo).
- [ ] **Nota 2026-10-07:** el pull de esa fecha trajo `clsServiciosConexosNexo.cs`/`clsServiciosConexosController.cs` nuevos (HU-013.3) — **verificar si esta issue ya quedó resuelta** antes de abrirla.
- [ ] **Revisión 2026-10-08:** cómo se **guardan** los conexos en Cotifly cambia: dejan de ser un JSON en la cabecera y la línea y pasan a una tabla por **ruta**, con su cantidad (HU-021.8). Eso no es parte de esta issue. Aquí queda lo que ya estaba: traer el catálogo y **enviar la selección a Nexo**. Como Nexo guarda los conexos pegados a una línea (que para él es un camión), los de una ruta se envían pegados a la línea del primer camión de esa ruta.

### [HU-015.4] (nota, no issue) Línea de negocio — confirmar si conviene pasar a catálogo en vivo

Esto está bien resuelto hoy: Cotifly usa su propio catálogo (`MargenesPolitica`) con un mapeo manual al código de Nexo (`LineaNegocioNexo`). Vale la pena preguntarle a Duban si ahora conviene consumir `GET /api/catalogos/lineas-negocio` en vivo en vez de mantener el mapeo a mano — no es un bug, es mantenimiento.

---

## HU-016 — Listado de cotizaciones por regional (Gerente Comercial)

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)

### [HU-016.1] Endpoint `GET /web/cotizaciones/regional`

**Labels:** `backend`
La vista de Gerente Comercial "Todas las cotizaciones" (frontend) necesita un listado fila por fila de las cotizaciones de los comerciales de su regional, pero hoy no existe:

- [ ] `GET /web/mis-cotizaciones` solo trae las del propio usuario (`clsCotizacionesController.cs:22`).
- [ ] `GET /web/metricas/regional` es **agregado** (`clsMetricasRegional`), no un listado fila por fila.
- [ ] Construir `GET /web/cotizaciones/regional` (mismo patrón de nombre y política `GerenteComercial` que ya usa `/web/metricas/regional`).
- [ ] Reusar `clsRegionalUsuarioService.ListarMiembrosAsync(regional)` (ya la usa el tablero de métricas) + un método nuevo en `IClsCotizacionRepository` (`ListarPorUsuariosAsync(IReadOnlyCollection<Guid> usuarioIds, ...)`) que filtre `WHERE usuarioId IN (...)` — no reconstruir la resolución de regional desde cero.
- [ ] **Revisión 2026-10-08:** se construye **después** de HU-021.4 y 021.9, sobre el repositorio y el resumen nuevos. La línea de negocio de una fila sale de la ruta 1 de la solicitud (o "Varias"), y el precio es el `precioFinal` de la cabecera.

---

## HU-017 — "Margen promedio" en Mis Cotizaciones: redefinir contra qué se compara

> **Repos:** `Cotifly` + `clientepro-frontend` · **Pendiente de decisión de negocio antes de construir**

### [HU-017.1] Decidir y construir la comparación correcta de margen

**Labels:** `backend`, `producto`
La card "Margen promedio" (Comercial) muestra un "± desviación" que casi siempre sale vacío (`—`). No es un bug de datos faltantes — es el comportamiento diseñado:

- [ ] `DesviacionPromedioMargen` (`clsCalculadoraMetricas.cs`) promedia `MargenAjustes.diferencia`, tabla que solo se escribe cuando el comercial **negocia explícitamente** un margen distinto (documentado así a propósito en ADR-016). Si el comercial siempre acepta el margen propuesto, la tabla queda vacía para él.
- [ ] **Lo que negocio quiere (pedido explícito de Sara, 2026-10-05):** comparar el margen aplicado promedio contra el **promedio de los márgenes de política de todas las líneas** (`MargenesPolitica.margen`), no contra los ajustes negociados. Verde si está por encima, rojo si está por debajo.
- [ ] **Opción A (más segura):** endpoint nuevo accesible para Comercial que liste `MargenesPolitica` (línea, margen) de las líneas activas, para que el frontend calcule el promedio/desviación. Hoy el único que expone esto (`GET /web/admin/lineas-negocio`) está protegido con política `GerenteComercial` — no hay ADR que declare esto confidencial para Comercial (de hecho ya se filtra uno por uno vía el chat, herramienta `obtener_margen`); el bloqueo actual parece ser de alcance administrativo, no de confidencialidad.
- [ ] **Opción B (más riesgosa):** cambiar la definición de `DesviacionPromedioMargen` en el backend — ADR-016 ya la define a propósito como "solo ajustes negociados", podría afectar otros reportes que dependen de esa definición.
- [ ] Decidir con negocio cuál opción tomar antes de construir.
- [ ] **Revisión 2026-10-08:** el margen aplicado ahora es **por ruta** (`CotizacionLineasSolicitud.margenAplicado`) y los ajustes también (`MargenAjustes.lineaSolicitudId`). "Margen aplicado promedio" de un comercial se calcula sobre sus rutas, no sobre sus cotizaciones. Con la Opción B, recordar que `DesviacionPromedioMargen` pasa a leer por ruta (HU-021.10).

---

## HU-018 — Enriquecer "Ver detalle" de una cotización

> **Repos:** `Cotifly` (018.1) + `clientepro-frontend` (consumo) · **Contexto:** se revisó `administracion_embarqa_nexo` (página `mesa-costeo`) como referencia de lo que el negocio probablemente espera ver.

### [HU-018.1] Ganancia rápida: exponer 4 campos que ya existen en BD pero el DTO no los expone

**Labels:** `backend`
`GET /web/mis-cotizaciones` (y "Ver detalle", que reusa la misma fila) usa `clsCotizacionResumen`, que **no incluye** `deadline`, `origen`, `destino`, `pesoKg` ni `valorMercancia` — aunque ya existen en `clsCotizacion` (desde el 0004) y **ya se exponen** en `GET /web/cotizaciones/{id}/nueva-version` → `clsDatosNuevaVersion`. El dato ya está, solo falta copiarlo al DTO de la lista/detalle.

- [ ] Agregar esos 4 campos a `clsCotizacionResumen` (o al contrato `CotizacionResumen`) — alimenta de una vez `/web/mis-cotizaciones`, `/seguimiento`, `/resultado`, `/web/solicitudes-costeo` y `/costeo/reintentar`.
- [ ] **Reformulada el 2026-10-08.** Con el esquema nuevo, `origen`, `destino`, `pesoKg` y `valorMercancia` **ya no están en la cabecera**: son de cada **ruta** (origen y destino como id de municipio, que hay que resolver a nombre con el catálogo de Nexo). Solo `deadline` sigue en la cabecera. Lo que se expone es: `deadline` + un resumen de las rutas (origen → destino, peso, valor de la mercancía, y los camiones que lleva cada una). Se construye junto con el resumen nuevo de **HU-021.9** y el contrato de la API de **HU-021.11**; no tiene sentido hacerlo antes, porque el DTO se reescribe. La parte del frontend ("Ver detalle") se redacta aparte.
- [ ] `scoreUrgencia` sigue genuinamente bloqueado (no existe en el backend, pendiente del contrato de Nexo para consultarlo por solicitud, ver ADR-016). Nota aparte: Nexo sí devuelve `scorePrioridad` al **crear** la solicitud (`POST /api/captura-solicitudes` → `CapturaResultDto`), pero Cotifly lo descarta hoy (`clsSolicitudCreadaNexo`) — capturarlo ahí sería una ganancia más chica y ya desbloqueada, distinta de "consultarlo después por solicitud".

### [HU-018.2] (catálogo de referencia, no construir todo de una — priorizar con negocio)

Lo que Nexo ya muestra en su "Mesa de Costeo" que Cotifly no muestra:

- **Nivel solicitud:** cliente, tipo, modalidad, deadline, valor estimado, probabilidad de cierre, `scorePrioridad`, estado, fecha de captura, notas.
- **Nivel línea:** ruta, tipología, peso, tipo de vehículo, carrocería, posiciones — Cotifly ya lo captura, el detalle no lo vuelve a mostrar. **(2026-10-08)** En el esquema nuevo esto se reparte: la **ruta** tiene origen, destino, tipología, peso y mercancías; el **camión** tiene vehículo, carrocería y los costos de Nexo.
- **Transparencia del costeo (lo más rico, lo que más se aleja de Cotifly hoy):** piso SICE-TAC desglosado, 3 fuentes candidatas lado a lado (SICE-TAC/plaza/histórico SAP, cada una con detalle y coincidencias), propuesta final con justificación en texto, histórico de tarifas de la ruta con vigencia. Cotifly hoy solo recibe un precio final — **falta confirmar con Duban si Nexo ya manda este desglose y se descarta, o si hay que pedirlo aparte.**
- **Servicios conexos:** lista con etiqueta de alcance (General/en N de M líneas/Individual) — cubierto por HU-015.3.
- **Cubicaje/vehículo sugerido:** un resumen compacto basta (el desglose 3D es herramienta de trabajo aparte).
- **Auditoría de quién hizo qué:** tampoco existe en el propio Nexo — si se quiere en Cotifly, no hay patrón que copiar, hay que diseñarlo desde cero.

**Prioridad sugerida:** 018.1 primero (bajo costo, ya resuelto el "por qué"); el desglose de costeo depende de confirmar si Nexo ya manda esos datos; el resto es telón de fondo para cuando negocio priorice.

---

## HU-019 — Ganada/Perdida: motivo + comentario separados (como pedía la HU original)

> **Repos:** `Cotifly` + `clientepro-frontend` · **Pendiente de decisión de negocio**

### [HU-019.1] Separar motivo (lista predefinida) de comentario (texto libre)

**Labels:** `backend`, `frontend`, `producto`
`POST /web/cotizaciones/{folio}/seguimiento` ya funciona, pero el cuerpo es `{ ganada, motivo_rechazo }` — un solo campo de texto libre.

> **Corrección 2026-10-08 — verificar antes de construir.** Revisando el backend, para las cotizaciones **del flujo de Nexo** `/seguimiento` las rechaza con `USAR_RESULTADO_NEXO` (`clsSeguimientoService.cs:46-52`) y el camino correcto es `POST /web/cotizaciones/{folio}/resultado` (`clsResultadoCotizacionService.cs`), que primero llama a Nexo y solo guarda local si Nexo responde 200. El frontend marca ganada/perdida con `/seguimiento` (`detalle-cotizacion.component.ts`). **Confirmar a cuál llama de verdad:** si es `/seguimiento`, el botón ya falla para las cotizaciones del flujo de Nexo y eso es un bug aparte, anterior a esta HU. Esta HU debe construirse sobre `/resultado`. Además, `motivoRechazo` debe quedar en "No aplica" cuando se marca ganada (regla de la propuesta original, pendiente en HU-021.13). La HU-004.5 original pedía 2 campos: **motivo** (lista predefinida, se persiste en Cotifly y se envía a Nexo) y **comentario** (texto libre, solo a Nexo). Nunca se terminó de construir — ni el contrato (`SeguimientoWebHttp` solo tiene `motivo_rechazo`) ni el frontend (`detalle-cotizacion.component.html` es un único `<textarea>`).

- [ ] Referencia: el frontend de Nexo (`mesa-costeo-page.component.ts`) ya tiene la lista de motivos predefinidos para perder: `Valor muy alto`, `Flete fuera de rango de plaza`, `Cliente desistió del servicio`, `Tiempo de respuesta vencido`, `Otro` — más un comentario libre aparte. Cotifly podría alinearse a esta misma lista.
- [ ] **Decidir con negocio:** ¿vale la pena construir motivo+comentario separados, o el texto libre actual es suficiente?
- [ ] Si se decide construir: agregar `comentario` a `SeguimientoWebHttp` (backend) + select de motivos + textarea de comentario (frontend).

---

## HU-020 — "Exportar Excel" en Ver Detalle

> **Repos:** `Cotifly` + `clientepro-frontend`

### [HU-020.1] Investigar y construir la exportación a Excel

**Labels:** `backend`, `frontend`, `investigacion`
El botón "Exportar Excel" en "Ver detalle" sigue deshabilitado (`title="Próximamente"`), placeholder desde que se construyó la pantalla — sin endpoint de backend. El frontend de Nexo sí tiene este botón funcional en su "Mesa de Costeo" (junto a "Descargar PDF"), pero no se investigó a fondo qué exporta ni contra qué endpoint.

- [ ] Investigar qué exporta exactamente Nexo y contra qué endpoint, antes de construir el lado de Cotifly.
- [ ] Definir el contrato: ¿la misma info del PDF, un desglose por línea, algo más?
- [ ] Construir el endpoint correspondiente en Cotifly + el consumo en frontend.
- [ ] **Revisión 2026-10-08:** el desglose "por línea" pasa a ser **por ruta, con sus camiones**. Ya existe una exportación de licitaciones (`clsLicitacionService`, que HU-021.6 rehace para el esquema nuevo): revisar si esa se reutiliza antes de construir una nueva.

---

## Sin incluir / descartado

- El manejo del prefijo de dominio (`boterosoto\usuario`) en el mecanismo de regional (`clsDirectorioEjecutivosDimEjecutivo`, PR #83) se revisó y **se ve correcto** — no se incluye como pendiente salvo que aparezca un caso raro concreto en pruebas reales.
- Nada sobre el envío de correo (`Correo:Habilitado`) — es config/credenciales del relay SMTP corporativo, no una corrección de código.

- **HU-014.2** (la cotización de prueba de Duban en la base compartida): ya no aplica desde el 2026-10-08, ver la tabla de arriba.

## Orden sugerido para el Sprint 5

1. **Primero HU-021 ([Corrección con BD])**, completa o al menos hasta HU-021.4: sin eso no hay backend funcionando.
2. HU-014.3 (commitear el header) **antes** de HU-021.7, para no mezclar dos cambios en `clsCosteoNexo.cs`.
3. HU-014.1 y 014.4, que ya están absorbidas o coordinadas con HU-021.
4. HU-015.1, 015.2 y 015.3, sobre la estructura nueva.
5. HU-016, 017, 018, 019 y 020, que dependen de los DTOs y el resumen nuevos (HU-021.9 y 021.11). HU-019.1 y la verificación de `/seguimiento` contra `/resultado` se pueden adelantar: son independientes de la estructura.

## Pendiente de agregar

- Los acuerdos con el equipo del cubicador y con Nexo (campo de ruta, códigos de vehículo, `TrqType` de `DevolucionContenedor`) están en `docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md` (lo que se le pide a Nexo y al cubicador; no son issues) y, para el `TrqType`, en HU-021.2 de `docs/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md`.
