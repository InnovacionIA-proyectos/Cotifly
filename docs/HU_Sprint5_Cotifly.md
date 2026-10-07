# Sprint 5 — Cotifly (HUs de backend y frontend)

> **Fecha de redacción:** 2026-10-07. Documento vivo: se va completando a medida que se van confirmando más hallazgos/pedidos para este sprint — no es la versión final todavía.
> **Fuente:** `docs/PENDIENTES_CORRECCION_DUBAN.md` (hallazgos encontrados probando en vivo contra `tbsdatacenter`/`dbTarifario_Nexo_Pruebas`, 2026-09-29 a 2026-10-05) + deuda técnica detectada aparte.
> **Convención:** sigue el mismo formato de `docs/HU_Backlog_Cotifly_ClientePro.md` (Historia / Contexto / checklist con `[ ]`), numeración continúa desde HU-013 (la de `ADR-018`, ya construida) — estas son todas nuevas, HU-014 en adelante.

---

## HU-014 — Deuda técnica y correcciones de backend

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)

### [HU-014.1] `estadoEnvio` sin `CHECK` en BD: un solo valor inválido tumba TODO "Mis cotizaciones" del usuario

**Labels:** `backend`, `bug`
**Síntoma:** `GET /web/mis-cotizaciones` respondió `500` para un usuario con una sola cotización con un valor fuera de catálogo (`estadoEnvio = 'pendiente'`, insertado por error en datos de prueba).

- [ ] **Causa:** `clsCotizacionRepository.ListarConLineaPorUsuarioAsync` (`src/Cotifly.Infraestructura/Cotizaciones/Repositories/clsCotizacionRepository.cs:109`) trae TODAS las cotizaciones del usuario en una sola consulta materializada. El mapeo de EF Core (`clsConvertidorCodigoEnum<TEnum>`) lanza `ArgumentException` sin capturar si el código no está en el enum (`EstadoEnvio`/`EstadoCosteo`) — la excepción no se maneja en ningún punto de la cadena, así que se propaga como `500` y bloquea la consulta completa para ese usuario, no solo la fila mala.
- [ ] **Por qué importa:** `estadoCosteo` ya tiene `CHECK` en BD desde el 0004 (`ck_cotizaciones_estadoCosteo`), pero `estadoEnvio` sigue sin ninguno — nada impide hoy que una fila quede fuera de `sin_enviar`/`enviada`/`reenviada` y tumbe el módulo para ese comercial.
- [ ] Corto plazo: agregar un `CHECK` para `estadoEnvio` igual al patrón de `ck_cotizaciones_estadoCosteo`.
- [ ] Mediano plazo: que `ListarConLineaPorUsuarioAsync` (o el converter) no deje que una fila con un código inesperado tumbe las demás — tolerar y loguear en vez de lanzar, o mapear fila por fila en vez de una proyección que materializa todo de una.

### [HU-014.2] Confirmar contra qué base insertó Duban su cotización de prueba

**Labels:** `backend`, `investigacion`
- [ ] La cotización de prueba que Duban insertó manualmente para probar "Mis cotizaciones" no apareció en `dbo_cotifly.Cotizaciones` de `tbsdatacenter`/`dbTarifario_Nexo_Pruebas` (la base real compartida) — confirmar contra qué servidor/base la insertó, para descartar una segunda base "de pruebas" desincronizada dando vueltas.

### [HU-014.3] Commitear como definitivo el ajuste del header `X-Portal-clsUsuario-Id`

**Labels:** `backend`, `tarea-tecnica`
**Decisión (2026-10-05):** Nexo no va a corregir el nombre del header de su lado (bug admitido en `CONTRATO.md §7`, discrepancia #1) — el ajuste queda **permanente** en Cotifly, no es un parche a revertir.

- [ ] El ajuste (`"X-Portal-Usuario-Id"` → `"X-Portal-clsUsuario-Id"`) ya está aplicado en el código de los adaptadores de Nexo (clientes, catálogos maestros, cubicación, costeo×2, municipios, análisis de Excel de licitaciones) y los comentarios ya no dicen "revertir cuando lo arreglen".
- [ ] **Falta:** commitear estos cambios como definitivos — hoy siguen sin commitear en la rama de trabajo.

### [HU-014.4] Retirar el borrador obsoleto del tablero de métricas (`/actividad`, `/ajustes-margen`)

**Labels:** `backend`, `tarea-tecnica`, `deuda-tecnica`
**Depende de que HU-010 (tablero real, `/web/metricas/comercial` y `/regional`) esté validado en producción sin depender de esto.**

`GET /web/metricas/actividad` y `GET /web/metricas/ajustes-margen` fueron el primer borrador del tablero, antes del diseño final de HU-010.1. Ya están marcados `(obsoleto)` en el propio código (`clsMetricasController.cs`) y `clientepro-frontend` no los consume en ningún lado (confirmado por búsqueda en el repo).

- [ ] Confirmar que ningún consumidor externo (Postman guardado, script, otro servicio) sigue llamando estos dos endpoints.
- [ ] Retirar los endpoints `Actividad`/`AjustesMargen` de `clsMetricasController.cs`.
- [ ] Retirar `ListarActividadAsync`/`ListarAjustesMargenAsync` de `IClsMetricasRepository`/`clsMetricasRepository.cs`, y los DTOs `clsActividadComercial`/`clsAjustesMargenComercial`.
- [ ] Retirar las vistas `vActividadPorComercial` (creada en 0001, redefinida en 0003) y `vAjustesMargenPorComercial` (creada en 0006) con un script `000N_retirar_vistas_actividad.sql` — no destructivo sobre datos (son vistas).
- [ ] Actualizar `docs/CONTRATO_API_WEB.md`/`specs/openapi.yaml` quitando ambas rutas.

---

## HU-015 — Catálogos y servicios conexos: usar las fuentes correctas de Nexo

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)
> **Contexto general:** revisando qué le falta a Cotifly para que toda la información del formulario de "Nueva cotización" se cree correctamente en las tablas de Nexo (no solo se calcule/muestre localmente), se comparó `CONTRATO.md` de Nexo (incluido lo nuevo bajo `/api/catalogos/*`) contra lo que Cotifly implementaba hasta el 2026-10-05.

### [HU-015.1] Migrar tipo de vehículo / clase de viaje al catálogo maestro de Nexo

**Labels:** `backend`
- [ ] Cotifly los traía de `GET /api/biblioteca-sap-historico/filtros` (pensado para filtrar histórico de tarifas, no un catálogo maestro) en vez de `GET /api/catalogos/tipos-vehiculo` y `GET /api/catalogos/clases-viaje`.
- [ ] **Nota 2026-10-07:** el pull de QA de esa fecha ya trajo la corrección (`clsCatalogosMaestrosNexo.cs`/`clsCatalogosFleteNexo.cs` reescritos, HU-013.10 #106) — **verificar si esta issue ya quedó resuelta** antes de abrirla, puede que solo falte confirmar en QA.
- [ ] Confirmar con Duban si existe un catálogo maestro equivalente para **carrocería**, o si ese campo se queda en `biblioteca-sap-historico/filtros` por ahora.

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

---

## HU-018 — Enriquecer "Ver detalle" de una cotización

> **Repos:** `Cotifly` (018.1) + `clientepro-frontend` (consumo) · **Contexto:** se revisó `administracion_embarqa_nexo` (página `mesa-costeo`) como referencia de lo que el negocio probablemente espera ver.

### [HU-018.1] Ganancia rápida: exponer 4 campos que ya existen en BD pero el DTO no los expone

**Labels:** `backend`
`GET /web/mis-cotizaciones` (y "Ver detalle", que reusa la misma fila) usa `clsCotizacionResumen`, que **no incluye** `deadline`, `origen`, `destino`, `pesoKg` ni `valorMercancia` — aunque ya existen en `clsCotizacion` (desde el 0004) y **ya se exponen** en `GET /web/cotizaciones/{id}/nueva-version` → `clsDatosNuevaVersion`. El dato ya está, solo falta copiarlo al DTO de la lista/detalle.

- [ ] Agregar esos 4 campos a `clsCotizacionResumen` (o al contrato `CotizacionResumen`) — alimenta de una vez `/web/mis-cotizaciones`, `/seguimiento`, `/resultado`, `/web/solicitudes-costeo` y `/costeo/reintentar`.
- [ ] `scoreUrgencia` sigue genuinamente bloqueado (no existe en el backend, pendiente del contrato de Nexo para consultarlo por solicitud, ver ADR-016). Nota aparte: Nexo sí devuelve `scorePrioridad` al **crear** la solicitud (`POST /api/captura-solicitudes` → `CapturaResultDto`), pero Cotifly lo descarta hoy (`clsSolicitudCreadaNexo`) — capturarlo ahí sería una ganancia más chica y ya desbloqueada, distinta de "consultarlo después por solicitud".

### [HU-018.2] (catálogo de referencia, no construir todo de una — priorizar con negocio)

Lo que Nexo ya muestra en su "Mesa de Costeo" que Cotifly no muestra:

- **Nivel solicitud:** cliente, tipo, modalidad, deadline, valor estimado, probabilidad de cierre, `scorePrioridad`, estado, fecha de captura, notas.
- **Nivel línea:** ruta, tipología, peso, tipo de vehículo, carrocería, posiciones — Cotifly ya lo captura, el detalle no lo vuelve a mostrar.
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
`POST /web/cotizaciones/{folio}/seguimiento` ya funciona, pero el cuerpo es `{ ganada, motivo_rechazo }` — un solo campo de texto libre. La HU-004.5 original pedía 2 campos: **motivo** (lista predefinida, se persiste en Cotifly y se envía a Nexo) y **comentario** (texto libre, solo a Nexo). Nunca se terminó de construir — ni el contrato (`SeguimientoWebHttp` solo tiene `motivo_rechazo`) ni el frontend (`detalle-cotizacion.component.html` es un único `<textarea>`).

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

---

## Sin incluir / descartado

- El manejo del prefijo de dominio (`boterosoto\usuario`) en el mecanismo de regional (`clsDirectorioEjecutivosDimEjecutivo`, PR #83) se revisó y **se ve correcto** — no se incluye como pendiente salvo que aparezca un caso raro concreto en pruebas reales.
- Nada sobre el envío de correo (`Correo:Habilitado`) — es config/credenciales del relay SMTP corporativo, no una corrección de código.

## Pendiente de agregar

_(space para lo que falta — se sigue completando en esta conversación)_
