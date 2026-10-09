# Sprint 5 — Cotifly backend

**Fecha:** 2026-10-08. **Repo de los issues:** `Cotifly`.
**Qué es este documento:** el **único documento de backend del Sprint 5**. Junta:
- Las HU que salieron de los pendientes de corrección de Duban (antes en `docs/backlog/2026-10-07_HU_Sprint5_Cotifly.md`): HU-014.3, 015.1, 016.1, 018.1 y 022.1, **revisadas contra el código el 2026-10-08** y corregidas con lo encontrado.
- La conexión con el cubicador (HU-023.1 a 023.5), según su contrato real (hoja de ruta del cubicador del 2026-10-08, sección 2).

## Notas de lectura

1. **Repo de todos los issues:** `Cotifly`. Roles: **Duban** = Backend, **Sara** = Datos, **Kevin** = QA.
2. **Las HU de la reestructuración (HU-021.1 a 021.14) ya existen y no se modifican.** Si el contrato del cubicador cambia algo de ellas, el arreglo va en una issue de este documento y se anota en "Relación con las HU que ya existen".
3. **Revisión del código (2026-10-08):** se revisó `origin/QA` y la rama `140-hu-02113-…`, que es la punta de todo lo de HU-021 (ramas 130 a 140) y **todavía no está en QA**. El estado de cada issue dice qué está hecho y qué falta.
4. **Si alguna de las HU del 07 de octubre ya existe en GitHub**, no se crea otra vez: se le agrega como comentario el "Estado (2026-10-08)" de su sección.
5. **Documentos hermanos:** frontend en `docs/backlog/2026-10-08_HU_Sprint5_Cotifly_Front.md`; peticiones al cubicador en `docs/conexion_nexo/2026-10-08_PETICIONES_AL_CUBICADOR.md`; reestructuración en `docs/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md` (HU-021).

**El Sprint 5 va DESPUÉS de HU-021 ([Corrección con BD]).** El backend necesita la estructura solicitud → rutas → camiones de HU-021 para casi todo lo de aquí.

---

## Resumen: 10 issues

| Issue | Estado (2026-10-08) | Depende de |
|---|---|---|
| **[HU-014.3]** Deuda técnica: header definitivo y retiro del borrador de métricas | Pendiente | HU-010 validada (parte B); antes de unir HU-021.7 (parte A) |
| **[HU-015.1]** Catálogos y servicios conexos de NEXO | **Cumplida**; lo único pendiente pasó a HU-023.1 | — |
| **[HU-016.1]** Gerente Comercial: listado regional y margen promedio | Pendiente | HU-021.4 y 021.9; decisión de negocio (parte B) |
| **[HU-018.1]** "Ver detalle": campos, ganada/perdida y exportar Excel | Parte B del backend **cumplida**; A y C pendientes | #138 (HU-021.11) |
| **[HU-022.1]** Cierre: limpieza del código viejo y documentación | Pendiente | **Al final de todo** |
| **[HU-023.1]** Cubicador: adaptador y catálogos | Nueva | — (antes del viernes 16) |
| **[HU-023.2]** Cubicador: transición "Calcular cubicaje" | Nueva | HU-023.1. **Viernes 16 de octubre** |
| **[HU-023.3]** Cubicador: base de datos y solicitud con camiones | Nueva | HU-023.1 |
| **[HU-023.4]** Cubicador: envío a NEXO por camión | Nueva, **bloqueada por NEXO** | HU-023.3 y NEXO |
| **[HU-023.5]** Cubicador: licitación | Nueva | HU-023.3 |

---

## HU-014 — Deuda técnica de backend

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** S · **Roles:** Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** equipo de backend, **quiero** dejar como definitivo el header que NEXO exige y retirar el borrador viejo del tablero de métricas, **para** no arrastrar cambios sin commitear ni endpoints y vistas que nadie usa.

### Contexto técnico
NEXO lee `X-Portal-clsUsuario-Id` (con un "cls" de más, bug de un renombrado masivo) y no lo va a corregir (decisión del 2026-10-05). Cotifly ya tiene el ajuste, pero sin commitear. Aparte, `/web/metricas/actividad` y `/web/metricas/ajustes-margen` fueron el primer borrador del tablero, están marcados `(obsoleto)` y nadie los consume.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: El header queda en el código
Dado los adaptadores de NEXO de Cotifly
Cuando se revisa la rama principal
Entonces todos mandan X-Portal-clsUsuario-Id y no queda ningún cambio local sin commitear

Escenario: El borrador del tablero ya no existe
Dado que el tablero real de HU-010 está validado en producción
Cuando se retira el borrador
Entonces /web/metricas/actividad y /web/metricas/ajustes-margen ya no responden
Y las vistas vActividadPorComercial y vAjustesMargenPorComercial ya no existen
```

### Trazabilidad (event storming)
Fuente: puntos 4 y 11 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md` y la deuda del tablero de HU-010.

### Alcance en este repo (`Cotifly`)
1. Header definitivo y rutas de documentos (HU-014.3, parte A).
2. Retiro del borrador de métricas (HU-014.3, parte B).

**HU también en:** ninguna.

#### Issues en `Cotifly`

### [HU-014.3] Deuda técnica de backend: header definitivo y retiro del borrador de métricas
**Labels:** `backend`, `tarea-tecnica`, `deuda-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-014 (milestone).** Reúne las antiguas HU-014.3 y HU-014.4. Son dos tareas pequeñas e independientes.

> **Estado (2026-10-08): pendiente.** En QA y en la rama de HU-021 el código sigue mandando `X-Portal-Usuario-Id`; el cambio solo está en 6 archivos sin commitear de la copia local. Los endpoints y las vistas del borrador siguen ahí. **Riesgo:** la rama de HU-021.7 (envío a NEXO, rama 134) también toca `clsCosteoNexo.cs`; commitear la parte A **antes** de unir las ramas de HU-021, para no mezclar los dos cambios.

**Parte A — Commitear como definitivo el header `X-Portal-clsUsuario-Id`.**
Decisión (2026-10-05): NEXO no va a corregir el nombre del header de su lado (bug admitido en `CONTRATO.md §7`, discrepancia #1). El ajuste queda **permanente** en Cotifly.

- [ ] El ajuste (`"X-Portal-Usuario-Id"` → `"X-Portal-clsUsuario-Id"`) ya está aplicado y los comentarios ya no dicen "revertir". **Son 6 archivos** (a 2026-10-08): `clsBuscadorClientesNexo.cs`, `clsCatalogosMaestrosNexo.cs`, `clsCosteoNexo.cs` (dos sitios), `clsCubicacionNexo.cs`, `clsMunicipiosNexo.cs` y `clsAnalizadorExcelNexo.cs`.
- [ ] **Commitearlos como definitivos**, antes de unir las ramas de HU-021 (en especial la de HU-021.7).
- [ ] El adaptador nuevo del cubicador (HU-023.1) **no** usa este header: autentica con `Authorization: Bearer <llave de Cotifly>`. `clsCubicacionNexo.cs` se retira en HU-023.2.
- [ ] Al commitear, aprovechar el mismo commit para corregir las rutas de documentos viejas dentro de `backend-dotnet/` (punto 11 de los pendientes de Duban, 28 archivos).

**Parte B — Retirar el borrador obsoleto del tablero de métricas (`/actividad`, `/ajustes-margen`).**
**Depende de que HU-010 (tablero real, `/web/metricas/comercial` y `/regional`) esté validado en producción sin depender de esto.** Estos dos endpoints ya están marcados `(obsoleto)` en el código (`clsMetricasController.cs`) y `clientepro-frontend` no los consume.

- [ ] Confirmar que ningún consumidor externo (Postman guardado, script, otro servicio) sigue llamándolos.
- [ ] Retirar los endpoints `Actividad` y `AjustesMargen` de `clsMetricasController.cs`.
- [ ] Retirar `ListarActividadAsync` y `ListarAjustesMargenAsync` de `IClsMetricasRepository`/`clsMetricasRepository.cs`, y los DTOs `clsActividadComercial` y `clsAjustesMargenComercial`.
- [ ] Retirar las vistas `vActividadPorComercial` y `vAjustesMargenPorComercial` con un script nuevo **`0010_retirar_vistas_actividad.sql`**, y quitarlas también de `db/scripts/01_crear_esquema.sql` (regla: toda migración nueva se refleja en el 01). El `0009` queda como respaldo y no se toca. Si esta tarea se hace **antes** de HU-021.10, se quitan en vez de adaptarlas.
- [ ] Actualizar `docs/docs_front/CONTRATO_API_WEB.md` y `specs/openapi.yaml` quitando ambas rutas.

---

## HU-015 — Catálogos y servicios conexos: usar las fuentes correctas de NEXO

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 · **Épica:** E3 — Integración Cotifly ↔ Nexo · **Talla:** S · **Roles:** Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** comercial, **quiero** que lo que escojo en el formulario (clase de viaje, clase de contenedor, servicios conexos) salga de los catálogos reales de NEXO y llegue a NEXO, **para** que la cotización que NEXO guarda sea la misma que le entregué al cliente.

### Contexto técnico
Hasta el 2026-10-05 Cotifly no usaba las fuentes correctas de NEXO: traía tipo de vehículo y clase de viaje de `GET /api/biblioteca-sap-historico/filtros` (pensado para filtrar histórico), usaba un catálogo propio de clase de contenedor y no le enviaba a NEXO la selección de conexos. Los pulls de QA del 2026-10-07 (HU-013.3 y HU-013.10, #106) trajeron casi todo.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Los catálogos salen de NEXO
Dado el formulario de "Nueva cotización"
Cuando se cargan la clase de viaje y la clase de contenedor
Entonces vienen de /api/catalogos de NEXO y no de una lista fija

Escenario: Los conexos llegan a NEXO
Dado una cotización con servicios conexos escogidos
Cuando se envía a NEXO
Entonces la cotización de NEXO queda con esos mismos conexos
```

### Trazabilidad (event storming)
Fuente: puntos 3.1 a 3.4 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`. Construido en HU-013.3 y HU-013.10.

### Alcance en este repo (`Cotifly`)
1. Verificar y cerrar catálogos y conexos (HU-015.1).

**HU también en:** el catálogo de clase de contenedor del frontend está en `clientepro-frontend` (HU-021-FE.11).

#### Issues en `Cotifly`

### [HU-015.1] Catálogos y servicios conexos de NEXO: verificar y cerrar
**Labels:** `backend`, `integración`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-015 (milestone).** Reúne las antiguas HU-015.1, 015.2 y 015.3.

> **Estado (2026-10-08): cumplida.** Revisado en QA y en la rama de HU-021: ya están los catálogos maestros de NEXO (`clsCatalogosMaestrosNexo`: `clases-viaje`, `clases-contenedor`, `tipos-vehiculo`, `lineas-negocio`), el catálogo de conexos (`GET /api/servicios-conexos`) y el **envío de la selección a NEXO** (`POST /api/cotizaciones/{id}/conexos`, `clsServiciosConexosNexo`). Lo único que quedaba, quitar la lista fija de clase de contenedor, **pasó a HU-023.1, parte B**. Lo de vehículo y carrocería ya no aplica: ahora los recomienda el cubicador. **Si se crea, se crea cerrada con este registro.**

- [x] **Tipo de vehículo y clase de viaje** contra el catálogo maestro (`/api/catalogos/tipos-vehiculo` y `/clases-viaje`). Con el cubicador, vehículo y carrocería salen de la recomendación; solo la **clase de viaje** sigue siendo del comercial.
- [x] **Clase de contenedor** contra `GET /api/catalogos/clases-contenedor`. La lista fija `clsCatalogosCaptura.ClasesContenedor` sigue conviviendo: quitarla es **HU-023.1, parte B**.
- [x] **Servicios conexos: enviar la selección a NEXO** con `POST /api/cotizaciones/{id}/conexos`.
- [x] **Cómo se guardan los conexos en Cotifly** (tabla por ruta, pegados a la línea del primer camión al enviarlos) es de **HU-021.8**.
- [ ] Cerrar el issue dejando este registro.

**Nota (antigua HU-015.4, no es issue):** Cotifly usa su propio catálogo de línea de negocio (`MargenesPolitica`) con un mapeo manual al código de NEXO (`LineaNegocioNexo`), y está bien. Se puede preguntar a Duban si conviene consumir `GET /api/catalogos/lineas-negocio` en vivo; es mantenimiento, no un bug.

---

## HU-016 — Gerente Comercial: listado regional y margen promedio

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** M · **Roles:** Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** Gerente Comercial, **quiero** ver fila por fila las cotizaciones de los comerciales de mi regional, y **como** comercial, **quiero** saber si mi margen promedio está por encima o por debajo de la política, **para** seguir la gestión comercial con datos que tengan sentido.

### Contexto técnico
La vista "Todas las cotizaciones" del frontend ya existe, pero no hay endpoint que la alimente: `GET /web/mis-cotizaciones` solo trae las del propio usuario y `GET /web/metricas/regional` es agregado. La tarjeta "Margen promedio" muestra un "± desviación" que casi siempre sale vacío porque compara contra los ajustes negociados (ADR-016), no contra la política.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Listado regional
Dado un Gerente Comercial de una regional con tres comerciales
Cuando consulta "Todas las cotizaciones"
Entonces ve las cotizaciones de los tres, una fila por cotización

Escenario: Margen promedio contra la política
Dado un comercial que nunca negoció un margen
Cuando ve "Margen promedio"
Entonces ve su margen comparado con el promedio de la política, en verde o en rojo
Y no un "—"
```

### Trazabilidad (event storming)
Fuente: puntos 5 y 6 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`. Se construye sobre HU-010 (tablero) y HU-021 (resumen por ruta).

### Alcance en este repo (`Cotifly`)
1. Endpoint del listado regional y margen promedio (HU-016.1).

**HU también en:** `clientepro-frontend` (pantalla regional en HU-021-FE.7; tarjeta de margen en HU-021-FE.11).

#### Issues en `Cotifly`

### [HU-016.1] Gerente Comercial: listado regional y margen promedio
**Labels:** `backend`, `producto`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-016 (milestone).** Reúne las antiguas HU-016.1 y HU-017.1. **Depende de HU-021.4 y 021.9** (repositorio y resumen nuevos; están en la rama de HU-021, todavía no en QA).

> **Estado (2026-10-08): pendiente.** No existen `GET /web/cotizaciones/regional` ni `ListarPorUsuariosAsync`, ni en QA ni en la rama de HU-021. La parte B sigue esperando la decisión de negocio.

**Parte A — Endpoint `GET /web/cotizaciones/regional`.**
- [ ] Construir `GET /web/cotizaciones/regional` (mismo patrón de nombre y política `GerenteComercial` que `/web/metricas/regional`).
- [ ] Reusar `clsRegionalUsuarioService.ListarMiembrosAsync(regional)` y un método nuevo en `IClsCotizacionRepository` (`ListarPorUsuariosAsync(IReadOnlyCollection<Guid> usuarioIds, ...)`) que filtre `WHERE usuarioId IN (...)`; no reconstruir la resolución de regional.
- [ ] Construirlo **sobre el repositorio y el resumen de HU-021** (rama de HU-021 unida a QA). La línea de negocio de una fila sale de la ruta 1 de la solicitud (o "Varias"), y el precio es el `precioFinal` de la cabecera.

**Parte B — "Margen promedio" en "Mis cotizaciones": contra qué se compara.** *(Pendiente de decisión de negocio antes de construir.)*
`DesviacionPromedioMargen` (`clsCalculadoraMetricas.cs`) promedia `MargenAjustes.diferencia`, tabla que solo se escribe cuando el comercial **negocia** un margen distinto (documentado así en ADR-016).

- [ ] **Lo que negocio quiere** (pedido explícito de Sara, 2026-10-05): comparar el margen aplicado promedio contra el **promedio de los márgenes de política de todas las líneas** (`MargenesPolitica.margen`). Verde si está por encima, rojo si está por debajo.
- [ ] **Opción A (más segura):** endpoint nuevo accesible para Comercial que liste `MargenesPolitica` (línea y margen) de las líneas activas, para que el cliente calcule el promedio. Hoy `GET /web/admin/lineas-negocio` exige la política `GerenteComercial`; no hay ADR que declare esos márgenes confidenciales para el Comercial (ya se filtran uno por uno por el chat, herramienta `obtener_margen`).
- [ ] **Opción B (más riesgosa):** cambiar la definición de `DesviacionPromedioMargen`. ADR-016 la define a propósito como "solo ajustes negociados"; podría afectar otros reportes.
- [ ] **Decidir con negocio cuál opción antes de construir.**
- [ ] **Con el esquema nuevo:** el margen aplicado es **por ruta** (`CotizacionLineasSolicitud.margenAplicado`) y los ajustes también (`MargenAjustes.lineaSolicitudId`). El margen promedio de un comercial se calcula sobre sus rutas y no sobre sus cotizaciones. **Si es promedio simple o ponderado por costo no está decidido**: el pendiente original hablaba de un promedio simple y HU-021.10 recomienda ponderar por costo. Confirmar con negocio antes de construir.

---

## HU-018 — "Ver detalle" de una cotización

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** M · **Roles:** Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`

### Historia
**Como** comercial, **quiero** ver en el detalle de una cotización todo lo que se cotizó (fecha límite, rutas, camiones), marcarla como ganada o perdida con motivo y comentario, y exportarla a Excel, **para** no tener que ir a NEXO a buscar esa información.

### Contexto técnico
"Ver detalle" hoy solo muestra folio, cliente, línea, precio, margen y estado; varios campos salen "Pendiente". El botón "Exportar Excel" está deshabilitado. La "Mesa de Costeo" de NEXO es la referencia de lo que el negocio espera ver.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: El detalle muestra las rutas
Dado una cotización con dos rutas
Cuando el comercial abre "Ver detalle"
Entonces ve la fecha límite y, por ruta, origen, destino, peso, valor y camiones

Escenario: Perdida con motivo y comentario
Dado una cotización del flujo de NEXO
Cuando el comercial la marca como perdida con un motivo y un comentario
Entonces se registra en NEXO y en Cotifly, sin error

Escenario: Exportar
Dado una cotización costeada
Cuando el comercial exporta a Excel
Entonces descarga un archivo con sus rutas y camiones
```

### Trazabilidad (event storming)
Fuente: puntos 7, 8 y 9 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`. Se construye sobre HU-021.9, HU-021.11 (#138) y HU-021.13.

### Alcance en este repo (`Cotifly`)
1. Campos del detalle, ganada/perdida y exportar Excel (HU-018.1).

**HU también en:** `clientepro-frontend` (detalle en HU-021-FE.7; motivo + comentario en HU-021-FE.11; Excel del detalle en HU-021-FE.6).

#### Issues en `Cotifly`

### [HU-018.1] "Ver detalle": campos nuevos, ganada/perdida y exportar Excel
**Labels:** `backend`, `producto`, `investigacion`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-018 (milestone).** Reúne las antiguas HU-018.1, 019.1 y 020.1. **Depende de #138 (HU-021.11, contrato de la API por ruta)** para la parte A.

> **Estado (2026-10-08):** **parte B del backend cumplida**: `POST /web/cotizaciones/{folio}/resultado` ya recibe `Motivo` y `Comentario` por separado, y en la rama de HU-021 el motivo queda en "No aplica" al ganar (HU-021.13). Lo que falla es del **frontend**: marca ganada/perdida con `/seguimiento`, que rechaza las cotizaciones del flujo de NEXO con `USAR_RESULTADO_NEXO` (va en HU-021-FE.11). **Parte A pendiente:** `clsCotizacionResumen` todavía no trae `deadline` ni el resumen de las rutas (el propio código dice que llega con #138), y `scorePrioridad` no se captura. **Parte C pendiente:** no hay exportación de una cotización.

**Parte A — Campos del detalle y desglose del costeo.**
- [ ] Verificar que, con **#138 (HU-021.11)**, el detalle expone `deadline` (cabecera) y el resumen de las rutas: origen → destino (municipios resueltos a nombre con el catálogo de NEXO), peso, valor de la mercancía y camiones. Si #138 no lo trae, agregarlo aquí.
- [ ] Capturar `scorePrioridad` cuando NEXO responde al **crear** la solicitud (`POST /api/captura-solicitudes` → `CapturaResultDto`), que hoy se descarta (`clsSolicitudCreadaNexo`). Es una ganancia pequeña y ya desbloqueada. Consultarlo después por solicitud (`scoreUrgencia`) sigue **bloqueado** por el contrato de NEXO (ADR-016).
- [ ] **Investigar con Duban** si NEXO ya manda el desglose del costeo (piso SICE-TAC con horas y vigencia, 3 fuentes candidatas lado a lado, propuesta final con su justificación, tarifas históricas de la ruta) y Cotifly lo descarta, o si hay que pedírselo aparte. Hoy Cotifly solo recibe un precio final. **No construir de una vez**: priorizar con negocio (ver "Referencia" abajo).

**Parte B — Ganada/perdida: motivo + comentario separados.**
- [x] El backend recibe motivo y comentario por separado en `POST /web/cotizaciones/{folio}/resultado` (`clsResultadoWebRequest(Resultado, Motivo, Comentario)`), que llama primero a NEXO y solo guarda local si NEXO responde 200. Es el camino correcto para las cotizaciones del flujo de NEXO; `/seguimiento` las rechaza con `USAR_RESULTADO_NEXO` (`clsSeguimientoService.cs:46-52`).
- [x] `motivoRechazo` queda en "No aplica" al marcar ganada (HU-021.13, en la rama de HU-021).
- [ ] **Decidir con negocio** si el motivo es texto libre o una **lista predefinida**. La de NEXO (`mesa-costeo-page.component.ts`): `Valor muy alto`, `Flete fuera de rango de plaza`, `Cliente desistió del servicio`, `Tiempo de respuesta vencido`, `Otro`. Si es lista, validar el motivo contra ella en `clsResultadoCotizacionService`.
- [ ] Avisar al frontend: el botón debe usar `/resultado`, no `/seguimiento` (HU-021-FE.11).

**Parte C — Exportar Excel.**
- [ ] Investigar qué exporta NEXO en su "Mesa de Costeo" y contra qué endpoint, antes de construir el lado de Cotifly.
- [ ] Definir el contrato: ¿la misma información del PDF, un desglose, algo más? Con el esquema nuevo el desglose es **por ruta, con sus camiones**.
- [ ] **Reusar la exportación de licitaciones** (`clsLicitacionService`; con el cubicador muestra los camiones de cada ruta, HU-023.5) antes de construir una nueva.

**Referencia (antigua HU-018.2; no construir de una vez, priorizar con negocio): lo que la "Mesa de Costeo" de NEXO ya muestra y Cotifly no.**
- *Nivel solicitud:* cliente, tipo, modalidad, deadline, valor estimado, probabilidad de cierre, `scorePrioridad`, estado, fecha de captura, notas.
- *Nivel línea:* ruta, tipología, peso, vehículo, carrocería, posiciones. En el esquema nuevo se reparte: la **ruta** tiene origen, destino, tipología, peso y mercancías; el **camión** tiene vehículo, carrocería y los costos de NEXO.
- *Transparencia del costeo:* ver el desglose de la parte A.
- *Servicios conexos:* lista con etiqueta de alcance (General / en N de M líneas / Individual); cubierto por HU-015.1 y HU-021.8.
- *Cubicaje y vehículo sugerido:* un resumen compacto basta; el desglose 3D es una herramienta de trabajo aparte.
- *Auditoría de quién hizo qué:* tampoco existe en el propio NEXO; si se quiere en Cotifly, hay que diseñarla desde cero.

---

## HU-022 — Cierre del proyecto: limpieza y documentación profesional

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 · **Épica:** E1 — Migración de Backend y Datos · **Talla:** M · **Roles:** Datos (Sara), Backend (Duban)
> **Repos donde se crean issues de esta milestone:** `Cotifly`
> **Va al final de todo el Sprint 5**, después de todas las HU anteriores (HU-021 y HU-023 incluidas).

### Historia
**Como** equipo, **quiero** quitar el código y la infraestructura que ya no se usan y dejar la documentación ordenada y profesional, **para** que quien llegue al proyecto entienda qué está vigente sin tropezar con el stack viejo.

### Contexto técnico
Al borrar Alembic (HU-021.2) apareció que el stack viejo en Python y su infraestructura de Postgres siguen en el repo sin usarse (punto 12 de los pendientes de Duban). Además, buena parte de la documentación describe ese stack o el esquema anterior. HU-021.14 pone los documentos al día con el esquema nuevo; esta HU los deja **profesionales** y quita lo que sobra. La lista de pendientes para el final está en `docs/backlog/2026-10-08_PENDIENTES_AL_FINALIZAR_CORRECCION_BD.md`.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: Sin código muerto
Dado el repo al final del Sprint 5
Cuando se busca el stack viejo (Python, Alembic, Postgres)
Entonces no queda nada que no se use, y el backend compila, las pruebas pasan y el despliegue sigue sano

Escenario: Documentación clara
Dado una persona nueva en el proyecto
Cuando abre docs/README.md
Entonces encuentra un índice con un solo lugar por tema y sin documentos obsoletos
```

### Trazabilidad (event storming)
Fuente: puntos 11 y 12 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`.

### Alcance en este repo (`Cotifly`)
1. Limpieza del código viejo y documentación profesional (HU-022.1).

**HU también en:** ninguna.

#### Issues en `Cotifly`

### [HU-022.1] Limpiar el código viejo y dejar la documentación profesional
**Labels:** `backend`, `datos`, `documentación`, `deuda-tecnica`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Sara (Datos) + Duban (Backend)
**Parte de HU-022 (milestone). Va al final de todo.**

> **Estado (2026-10-08): pendiente.** Es para el final.

- [ ] **Evaluar qué quedó viejo y sin uso** (Python viejo, Alembic, Postgres, servicios de compose, scripts y pruebas del stack viejo) con un inventario: qué existe, quién lo usa y si está en algún ambiente.
- [ ] **Borrar por partes** y comprobar después de cada una que el backend compila, las pruebas pasan y el despliegue sigue sano.
- [ ] **Inventario de documentos** (`docs/`, `backend-dotnet/docs/`, `specs/`, `README.md`, `BASE_DE_DATOS.md`, `DEPLOY.md`, `infra/`, `db/README.md`): marcar cada uno como *vigente*, *por corregir* u *obsoleto*.
- [ ] **Borrar lo obsoleto**: documentos del stack viejo o propuestas ya ejecutadas y superadas. Antes de borrar, confirmar que nada vigente los enlaza.
- [ ] **Un solo lugar por tema** (esquema, API, despliegue, integración con NEXO y el cubicador, backlog) y un índice claro en `docs/README.md` y en el `README.md` de la raíz.
- [ ] **Estilo uniforme**: mismos encabezados, fecha y dueño, idioma y tono; sin texto de trabajo, tachados ni notas de sesión.
- [ ] **HU y notas de trabajo** (`docs/backlog/`): dejar las vigentes y archivar o borrar las cerradas.
- [ ] Revisar que **todos los enlaces y rutas** funcionen (incluye las 28 referencias de `backend-dotnet/`, si no se corrigieron ya en HU-014.3).
- [ ] Revisión final por alguien que **no** haya escrito el documento: ¿se entiende sin contexto?

---

## HU-023 — Conexión de Cotifly con el cubicador

> **HU en Notion:** _(pendiente — crear y pegar el link)_
> **Sprint:** Sprint 5 · **Épica:** E3 — Integración Cotifly ↔ Nexo · **Talla:** L · **Roles:** Backend (Duban), QA (Kevin)
> **Repos donde se crean issues de esta milestone:** `Cotifly`
> **Fecha que no se mueve:** el **viernes 16 de octubre de 2026** es el corte conjunto: el cubicador retira la cubicación que hoy usa Cotifly por medio de NEXO. **HU-023.2** tiene que estar ese día.

### Historia
**Como** comercial, **quiero** que Cotifly le pida al cubicador los camiones de cada ruta de mi solicitud, **para** no digitar el vehículo y para que cada camión se cotice en NEXO con el peso real que lleva.

### Contexto técnico

**Cómo queda la conexión:**
```
Navegador (ClientePro) ──► Backend de Cotifly ──► Cubicador   (Bearer con llave propia, origenSistema = COTIFLY)
                                  └────────────► NEXO         (una línea por camión, con GrupoRutaId)
```
- El navegador **nunca** llama al cubicador ni a NEXO.
- El cubicador **solo le responde a Cotifly**: no hace nada en NEXO. Cotifly guarda la recomendación y después le manda la solicitud a NEXO.
- Cotifly solo **lee** los catálogos del cubicador; no edita ninguno.

**Lo que se encontró en el código de Cotifly (2026-10-08):**
- La cubicación de hoy (`clsCubicacionNexo`, `POST /web/cubicacion`) llama a `POST /api/cubicacion` de NEXO. Ese endpoint se retira el viernes 16, sin transición. Si Cotifly no cambia, el comercial ve "sin vehículo sugerible" **sin ningún error en los logs**: `System.Text.Json` deja `null` lo que no reconoce.
- Esa cubicación usa el cliente HTTP de NEXO, con `AddStandardResilienceHandler`, que **reintenta los `POST`**. Con el cubicador cada reintento crea una corrida duplicada.
- `clsValidadorCatalogosCaptura` valida la carrocería contra una lista fija (`clsCatalogosCaptura.Carrocerias`: `ESTACAS`, `FURGON`, `FURGON REFRIGERADO`…) y el tipo de vehículo contra el catálogo de NEXO. Rechazaría los códigos del cubicador (`2L2`, `C2`, `C3`, `2S2`, `3S3`; `CAMA_BAJA`; el refrigerado como `FURGON` más una alerta).
- La licitación ya tiene `POST /web/licitaciones/analizar-excel`, `POST /web/licitaciones` y la exportación, pero las filas traen la mercancía como texto libre, sin código del catálogo.
- `/health/ready` solo revisa la base de datos.

**Lo que se encontró en el código de NEXO (revisión de la sesión de NEXO, 2026-10-08):**
- El DTO real de la línea de captura es **`clsCapturaLineaInput`** (`clsCapturarSolicitudCommand.cs:22-31`). Hoy **no** tiene `GrupoRutaId` ni `idRecomendacionVehiculo`, y `tbl_lineas_solicitud` tampoco.
- NEXO **no tiene cuarentena** de vehículos: si recibe un vehículo o carrocería que no conoce, **crea una fila nueva en su catálogo oficial** sin marcarla (`clsResolucionDeCatalogos.cs:54-68`). Si Cotifly manda los códigos del cubicador antes de que NEXO construya la cuarentena, ensucia ese catálogo.
- El orden de las líneas (`numeroLinea`) **no se guarda**: es un contador mientras procesa. Para ordenar las líneas de una misma ruta hace falta una columna de orden en NEXO.
- Ninguno de estos cambios de NEXO tiene una HU escrita todavía.

### Decisiones tomadas (2026-10-08)
1. **Mercancía por código.** A `POST /recomendaciones` se manda `codigoMercancia` (por ejemplo `BEBIDAS`), no el uuid. Cotifly guarda el código.
2. **Sin vista previa en el flujo final.** Los camiones se piden al guardar la ruta, con el id real de la ruta. Durante la transición (HU-023.2), "Calcular cubicaje" manda un id provisional `PRUEBA-<guid>`, y Cotifly no guarda nada de esas llamadas.
3. **Cotifly nunca toma "la última corrida".** Copia los `idRecomendacionVehiculo` de la respuesta a la llamada hecha con el id de su ruta.
4. **El nombre del vehículo no se guarda.** Se consulta por código en el catálogo de vehículos del cubicador (petición P1 al cubicador).
5. **No se guardan** `alertaBajoLlenado` (el cubicador no recomienda camiones con bajo llenado) ni una marca de condicionada por mercancía (basta la de la ruta).
6. **Licitación:** el tipo de mercancía de cada fila se **sugiere** por parecido de nombre con el catálogo; el comercial lo confirma o lo cambia.
7. **La base de datos la cambia backend** (Duban), con un script `02` de `ALTER TABLE`.
8. **HU-015.1 ya está cumplida:** los catálogos maestros de NEXO y el envío de los conexos a NEXO ya están en el código. Lo único que le quedaba, quitar la lista fija de clase de contenedor, pasó a HU-023.1, parte B.

### Criterios de aceptación (Gherkin)
```gherkin
Escenario: El día del corte
Dado que el cubicador retiró POST /api/cubicacion de NEXO
Cuando el comercial usa "Calcular cubicaje"
Entonces ve la recomendación nueva y no un "sin vehículo sugerible" falso

Escenario: Una ruta con flota mixta
Dado una ruta con mercancías del catálogo del cubicador
Cuando el comercial guarda la solicitud
Entonces Cotifly llama una vez al cubicador por esa ruta, con el id de la ruta y origenSistema COTIFLY
Y guarda un camión por cada vehículo recomendado, aunque sean de tipos distintos
Y cada camión queda con su código de vehículo, carrocería, peso, volumen y los dos porcentajes de llenado

Escenario: El cubicador no puede escoger camión
Dado una ruta con una mercancía extradimensionada
Cuando el cubicador responde SIN_SELECCION_AUTOMATICA con su motivo
Entonces la ruta queda con ese resultado y ese motivo
Y la solicitud se envía igual a NEXO con el camión sin vehículo

Escenario: Respuesta incompleta del cubicador
Dado que el cubicador responde ASIGNADO pero a un vehículo le falta el peso
Cuando Cotifly lee la respuesta
Entonces la rechaza con un error claro y no guarda nada

Escenario: Falla una de varias rutas
Dado una solicitud de 3 rutas
Cuando el cubicador falla en la tercera
Entonces no se guarda ninguna ruta ni se envía nada a NEXO
Y el comercial ve un mensaje claro

Escenario: Licitación con mercancía sugerida
Dado un Excel de licitación con la mercancía escrita como texto
Cuando el comercial lo sube
Entonces cada fila trae la mercancía más parecida del catálogo, marcada como sugerida
```

### Trazabilidad (event storming)
Fuentes: la hoja de ruta del cubicador del 2026-10-08 (contrato, sección 2; corte conjunto, sección 5), la revisión del código de Cotifly del 2026-10-08 y la revisión del código de NEXO de la misma fecha. Se construye sobre HU-021 (reestructuración: "una ruta = un camión" con el vehículo digitado) y sobre HU-015.1 (catálogos y conexos de NEXO, cumplida). Depende del cubicador (equipo de NEXO) y de NEXO (`GrupoRutaId`, `idRecomendacionVehiculo`, cuarentena, columna de orden).

### Alcance en este repo (`Cotifly`)
1. Adaptador del cubicador y catálogos (HU-023.1).
2. Transición del viernes 16: "Calcular cubicaje" contra el cubicador (HU-023.2).
3. Base de datos y solicitud con camiones (HU-023.3).
4. Envío a NEXO por camión (HU-023.4, bloqueada por NEXO).
5. Licitación con cubicador (HU-023.5).

**HU también en:** `clientepro-frontend` (frontend de esta conexión). Lo que se le pide al cubicador está en `PETICIONES_AL_CUBICADOR.md`; lo que tiene que construir NEXO no tiene HU todavía (ver "Dependencias externas").

#### Issues en `Cotifly`

### [HU-023.1] [Cubicador] Adaptador del cubicador y catálogos
**Labels:** `cotifly`, `backend`, `integración`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-023 (milestone). No depende de otras issues de esta milestone. Tiene que estar antes del viernes 16** (la usa HU-023.2). Reemplaza el enfoque de HU-021.5 (ver "Relación con las HU que ya existen").

**Parte A — Adaptador y configuración.**
- [ ] **Puerto y adaptador nuevos:** `IClsCubicador` (Aplicación) y `clsCubicadorHttp` (Infraestructura), con la versión "no disponible" cuando falta configuración (mismo patrón de fábrica que hoy).
- [ ] **Cliente HTTP propio `"Cubicador"`, sin reintento de `POST`.** Tiempo de espera de unos **30 s** (el objetivo del cubicador es 10 s). Los `GET` de catálogo sí pueden reintentar.
- [ ] **Autenticación:** `Authorization: Bearer <llave de Cotifly>`, configurada una vez en el cliente. **No** usa `X-Portal-clsUsuario-Id`. Configuración `Cubicador:UrlBase` y `Cubicador:ApiKey`, validada al arrancar.
- [ ] **DTOs del contrato.** Petición: `idLineaSolicitud`, `origenSistema = COTIFLY`, `claseViajeSeleccionada` (opcional, es una preferencia), `usuarioId` (el `idUsuarioExterno` del comercial, en texto) y `mercancias[]` con `codigoMercancia`, `cantidad`, `largoCm`, `anchoCm`, `altoCm` y `pesoKg` (por unidad). Respuesta: `idRecomendacion`, `resultado`, `motivoSinSeleccion`, `condicionada`, `mercanciasCondicionadas[]`, `vehiculos[]`, `advertencias[]` y `extradimension`.
- [ ] **Contra el fallo silencioso:** cuando `resultado = ASIGNADO`, exigir en cada vehículo `idRecomendacionVehiculo`, `codigoVehiculo`, `codigoCarroceria`, `pesoTotalKg`, `volumenTotalM3`, `porcentajeLlenadoVolumen` y `porcentajeLlenadoPeso`. Si falta alguno, la respuesta se rechaza con un error de Cotifly (`CUBICADOR_RESPUESTA_INVALIDA`) y no se guarda nada.
- [ ] **Resultados que no son error:** `SIN_SELECCION_AUTOMATICA` (con `motivoSinSeleccion`: `EXTRADIMENSIONADO`, `SIN_CANDIDATOS_DIMENSIONALES`, `SUPERA_K_MAX`, `SIN_CARROCERIA_COMPATIBLE`) y condicionada vuelven con 200.
- [ ] **Traducir los errores** a `clsCodigosError`: 400 `MERCANCIAS_REQUERIDAS`, `MERCANCIA_DESCONOCIDA`, `MERCANCIA_NO_CUBICABLE`, `DIMENSIONES_INVALIDAS`; 401 `NO_AUTORIZADO`; 503 `CUBICADOR_NO_DISPONIBLE`; tiempo agotado y red, no disponible. Extender el catálogo de códigos que ya existe.
- [ ] **Health check** del cubicador en `/health/ready` (si el cubicador expone un endpoint de salud, petición P7).
- [ ] **Despliegue:** la llave por ambiente (QA y producción) en los secretos de Dokploy; `DEPLOY.md` con las variables nuevas. La llave nunca llega al navegador.

**Parte B — Catálogos y códigos.**
- [ ] **`GET /web/tipos-mercancia`:** pasa el catálogo `GET /tipos-mercancia` del cubicador, con caché (patrón de `clsCatalogosMaestrosEnCache`). Devuelve código, nombre, categoría, `cubicable`, `condicionadaCubicacion`, `comentarioCondicion` y `activo`.
- [ ] **`GET /web/tipos-carroceria`** y, si el cubicador lo expone (petición P1), **`GET /web/tipos-vehiculo`**, con caché. El nombre del vehículo que se ve en pantalla y en el PDF sale de este catálogo.
- [ ] **Validación de códigos:** `clsValidadorCatalogosCaptura` debe aceptar los códigos de vehículo y carrocería del cubicador, validándolos contra su catálogo. La lista fija (`clsCatalogosCaptura.Carrocerias`) y el catálogo de NEXO quedan solo para el vehículo digitado como respaldo.
- [ ] **Clase de contenedor:** quitar la lista fija `clsCatalogosCaptura.ClasesContenedor` (5 códigos) y validar solo contra `GET /api/catalogos/clases-contenedor` de NEXO, que ya se consume (`clsCatalogosMaestrosNexo`). Conservar la lista provisional de extradimensionado mientras NEXO no la formalice. Viene de HU-015.1: es lo único que le quedaba pendiente.

**Parte C — Pruebas.**
- [ ] **Cubicador falso** con los ejemplos del contrato (petición P2): flota mixta, cada motivo de "sin selección", condicionada, extradimensión y cada error.
- [ ] Pruebas del adaptador: respuesta incompleta rechazada, `401` y `503` traducidos, sin reintento del `POST`, tiempo agotado.

### [HU-023.2] [Cubicador] Transición previa al corte: "Calcular cubicaje" contra el cubicador
**Labels:** `cotifly`, `backend`, `integración`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-023 (milestone). Depende de HU-023.1. Fecha: viernes 16 de octubre.** Sale junto con la transición del frontend. **No espera** a nada más: funciona sobre el formulario de hoy.

- [ ] `POST /web/cubicacion` pasa a llamar a `POST /recomendaciones` del cubicador por el adaptador de HU-023.1, con `idLineaSolicitud = "PRUEBA-<guid>"`. Cotifly no guarda nada de esa llamada.
- [ ] La entrada pasa a llevar `codigoMercancia` por ítem (hoy lleva solo `descripcion`). Mientras el frontend no tenga el desplegable, aceptar también la descripción y resolverla al código más parecido del catálogo (misma lógica de HU-023.5).
- [ ] La respuesta deja de ser "un vehículo o N iguales" (`clsResultadoCubicacion`, `clsSugerenciaFlota`) y pasa a ser una **lista de vehículos**, cada uno con código, nombre, carrocería, peso, volumen y los dos porcentajes, más el resultado, el motivo, la marca de condicionada, las advertencias y la extradimensión.
- [ ] El backend deja de devolver la clase de viaje y la clase de contenedor sugeridas: el cubicador ya no las sugiere.
- [ ] Retirar `clsCubicacionNexo`, `IClsCubicacionProvider` y su registro en `clsDependenciasInfraestructura`.
- [ ] Actualizar `specs/openapi.yaml` y `docs/docs_front/CONTRATO_API_WEB.md` para `POST /web/cubicacion`.
- [ ] **Verificación antes del corte**, contra el cubicador de QA (con Kevin): "Calcular cubicaje" con flota mixta; un código de mercancía desconocido se rechaza con un mensaje claro.
- [ ] **Riesgo de la fecha:** NEXO tampoco tiene construido el consumo de `/recomendaciones`, así que si el 16 se retira `POST /api/cubicacion`, NEXO también se queda sin cubicar. Confirmar la fecha real del corte con NEXO y con el cubicador antes de comprometerla.

### [HU-023.3] [Cubicador] Base de datos y solicitud con camiones
**Labels:** `cotifly`, `backend`, `integración`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-023 (milestone). Depende de HU-023.1.** Es el flujo final: reemplaza la vista previa de HU-023.2.

**Parte A — Script `02` de la base de datos.** Por el momento los cambios van en un script aparte, **`db/scripts/02_alter_cubicador.sql`**, con `ALTER TABLE`, y se documentan. Más adelante se consolidan en `01_crear_esquema.sql`.

| Tabla | Cambio | Para qué |
|---|---|---|
| `CotizacionMercancias` | `idTipoMercancia` (uuid) se reemplaza por **`codigoMercancia NVARCHAR(64) NULL`** | El cubicador recibe el código, que no cambia aunque resiembren el catálogo. Queda `NULL` en las filas viejas; el backend lo exige en las nuevas |
| `CotizacionVehiculosLineaSolicitud` | **`volumenTotalM3 DECIMAL(12,3) NULL`** | Volumen que lleva el camión |
| | **`porcentajeLlenadoVolumen DECIMAL(5,2) NULL`** y **`porcentajeLlenadoPeso DECIMAL(5,2) NULL`**, con `CHECK` de 0 a 100 | Qué tan lleno va cada camión |
| `CotizacionLineasSolicitud` | **`idRecomendacion UNIQUEIDENTIFIER NULL`** | La corrida del cubicador, para volver a consultarla |
| | **`resultadoCubicacion NVARCHAR(30) NULL`**, con `CHECK` (`ASIGNADO`, `SIN_SELECCION_AUTOMATICA`) | Qué respondió el cubicador por esa ruta |
| | **`motivoSinSeleccion NVARCHAR(40) NULL`**, con `CHECK` de los 4 motivos | Por qué no pudo escoger |
| | **`condicionada BIT NOT NULL DEFAULT 0`** | Si la ruta va a la Mesa de Costeo por mercancía condicionada |

**No se crean, a propósito:** el nombre del vehículo, la alerta de bajo llenado y una marca de condicionada por mercancía. **No cambian:** `tipoVehiculo` y `carroceria` del camión (ahora guardan los códigos del cubicador) y `pesoKg` (recibe `pesoTotalKg`).

- [ ] Escribir `db/scripts/02_alter_cubicador.sql`: idempotente (cada cambio verifica si ya está), en una transacción, con `CHECK` con nombre (`ck_<tabla>_<campo>`).
- [ ] **Antes de quitar `idTipoMercancia`, revisar si tiene datos** en la base compartida: un uuid no se puede convertir a código.
- [ ] Probarlo en el SQL Server de Docker sobre una base recién instalada con el 01, y correrlo dos veces para comprobar que no falla la segunda.
- [ ] Decidir si `db/ejecutar-bd.ps1` / `.sh` corren el `02` después del `01` en una instalación nueva, y documentarlo en `db/README.md`.
- [ ] Correrlo en la base compartida.
- [ ] Mapear las columnas nuevas en EF (`clsCotizacionConfiguracion.cs` y entidades).
- [ ] Documentar las columnas en `docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md` (§4 a §6, donde hoy dice `idTipoMercancia` y "sin código estable") y en el diagrama entidad-relación.

**Parte B — Solicitud con camiones.**
- [ ] En `POST /web/solicitudes-costeo`: por cada ruta, **una llamada** al cubicador con el id de la ruta, sus mercancías con `codigoMercancia` y la clase de viaje como `claseViajeSeleccionada`. El DTO de la petición pasa de `idTipoMercancia` a `codigoMercancia`.
- [ ] **Todo o nada:** primero se piden los camiones de todas las rutas y al final se guarda todo en una sola transacción. Si una ruta falla, no se guarda nada ni se envía nada a NEXO. Las corridas que el cubicador ya creó quedan huérfanas allá, y se acepta.
- [ ] Guardar un camión por vehículo recomendado: `idRecomendacionVehiculo`, `tipoVehiculo` (= `codigoVehiculo`), `carroceria` (= `codigoCarroceria`), `pesoKg` (= `pesoTotalKg`), `volumenTotalM3` y los dos porcentajes.
- [ ] Guardar en la ruta `idRecomendacion`, `resultadoCubicacion`, `motivoSinSeleccion` y `condicionada`. La lista `mercanciasCondicionadas[]` se devuelve al comercial pero no se guarda.
- [ ] **Sin selección:** un camión sin vehículo ni carrocería, que se envía igual a NEXO y lo resuelve la Mesa de Costeo.
- [ ] Devolver al comercial las `advertencias[]` y la extradimensión en la respuesta (no se guardan).
- [ ] **Recalcular una ruta** (`POST /web/cotizaciones/{folio}/rutas/{rutaId}/recalcular-vehiculos`): cuando cambian sus mercancías o su peso, pide de nuevo los camiones y reemplaza los de esa ruta. Usarlo también en la nueva versión y la re-solicitud.
- [ ] **Reconsultar** qué lleva cada camión (`GET /recomendaciones/{idRecomendacionVehiculo}/mercancias`), si el detalle lo necesita.
- [ ] `GET /web/cotizaciones/{folio}/costeo` devuelve, por ruta, el resultado del cubicador y, por camión, el volumen y los dos porcentajes.
- [ ] **Agente de chat:** no captura mercancías, así que no llama al cubicador. Su ruta queda con un camión sin recomendación y el vehículo que diga la conversación.
- [ ] **Mercancías viejas** sin código: si hay que recalcular, el comercial las vuelve a escoger.
- [ ] Actualizar `specs/openapi.yaml` y `docs/docs_front/CONTRATO_API_WEB.md` con los endpoints nuevos y cambiados.

**Parte C — Pruebas.**
- [ ] Todo o nada con varias rutas, flota mixta guardada, recalcular una ruta y "sin selección" enviado a NEXO sin vehículo.
- [ ] Prueba de punta a punta contra el cubicador de QA: guardar una solicitud con flota mixta y comprobar que el `idRecomendacionVehiculo` guardado devuelve la misma recomendación al reconsultarlo (con Kevin).

### [HU-023.4] [Cubicador] Envío a NEXO por camión con los datos del cubicador
**Labels:** `cotifly`, `backend`, `integración`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-023 (milestone). Depende de HU-023.3. Bloqueada por NEXO:** hoy NEXO no tiene ninguno de los campos que necesita (ver "Dependencias externas").

- [ ] Una línea por camión con los códigos de vehículo y carrocería del cubicador y su peso.
- [ ] **Bandera para los códigos:** mientras NEXO no tenga la cuarentena de vehículos, **no** mandarle los códigos del cubicador (crearía filas nuevas en su catálogo oficial). Hasta entonces se manda el vehículo como hoy. La bandera se enciende cuando NEXO confirme que la cuarentena está en producción. *(Decisión pendiente de confirmar: es la opción recomendada; la alternativa es una tabla de equivalencias en Cotifly.)*
- [ ] **`GrupoRutaId`** = id de la ruta en todas las líneas de esa ruta. Lo pone Cotifly; NEXO solo lo guarda y lo devuelve en `GET /api/solicitudes/{id}`. Va en **`clsCapturaLineaInput`** de NEXO.
- [ ] **`idRecomendacionVehiculo`** en cada línea, también en `clsCapturaLineaInput`.
- [ ] Mandar las líneas de una misma ruta **seguidas** y, cuando NEXO tenga la **columna de orden**, con su número de orden, para que los conexos queden en la línea del primer camión.
- [ ] Emparejar los camiones con las líneas de NEXO usando `GrupoRutaId` cuando NEXO lo devuelva; mientras no, por contenido y orden de envío.
- [ ] Pruebas contra un NEXO falso: dos camiones iguales en la misma ruta (el caso frágil), con y sin `GrupoRutaId`.

### [HU-023.5] [Cubicador] Licitación con cubicador
**Labels:** `cotifly`, `backend`, `integración`
**Repo donde se crea este issue:** `Cotifly`
**Asignado a:** Duban (Backend)
**Parte de HU-023 (milestone). Depende de HU-023.3.**

- [ ] Al analizar el Excel (`POST /web/licitaciones/analizar-excel`), sugerir para cada fila el **`codigoMercancia` más parecido** del catálogo: comparar el texto de la mercancía con los nombres del catálogo, sin tildes ni mayúsculas, y escoger el que más se parezca. Si no hay nada parecido, queda vacío.
- [ ] Devolver cada sugerencia **marcada como "sugerida"**, para que el frontend la muestre así y el comercial la confirme o la cambie.
- [ ] Si el Excel trae una columna con el código, usarla directamente.
- [ ] **Cubicar todas las rutas en segundo plano** y exponer el progreso por ruta: una licitación de muchas rutas son muchas llamadas de hasta 10 s cada una. Respetar el límite de llamadas en paralelo que diga el cubicador (petición P6).
- [ ] La exportación del Excel (`GET /web/licitaciones/{folio}/excel`) muestra los camiones de cada ruta. La exportación de una cotización desde "Ver detalle" (HU-018.1, parte C) debe reusar esta.
- [ ] Pruebas: sugerencia por parecido (con tildes, mayúsculas y sin coincidencia), licitación de varias rutas cubicada en segundo plano.

---

## Relación con las HU que ya existen (no se modifican)

| HU que ya existe | Qué dice | Qué vale ahora |
|---|---|---|
| HU-021.5 Integración con el cubicador | Un adaptador con `idTipoMercancia`, pensado antes del contrato | **Lo reemplaza HU-023.1**, con el contrato real (`codigoMercancia`, Bearer, campos obligatorios). Lo que se haya construido de HU-021.5 se adapta en HU-023.1 |
| HU-021.4 Persistencia y `01_crear_esquema.sql` | `CotizacionMercancias.idTipoMercancia` | **HU-023.3, parte A** lo cambia a `codigoMercancia` con el script `02` y agrega las columnas del cubicador |
| HU-021.6 Captura | "Mientras el cubicador no exista: una ruta = un camión, con el vehículo digitado" | **HU-023.3, parte B** conecta el cubicador al guardar. La validación de códigos se arregla en **HU-023.1, parte B**. La licitación con cubicador está en **HU-023.5** |
| HU-021.7 Envío a NEXO | Una línea por camión, emparejador | **HU-023.4** agrega `GrupoRutaId`, `idRecomendacionVehiculo` y la bandera de códigos |
| HU-021.11 Contrato de la API (#138) | DTOs con `idTipoMercancia` | **HU-023.3, parte B** pasa a `codigoMercancia` y actualiza `openapi.yaml` |
| HU-021.12 Pruebas | Cubicador falso y prueba de punta a punta | Cada issue de HU-023 trae sus pruebas |

## Dependencias externas (no son issues de Cotifly)

| Con quién | Qué | Bloquea |
|---|---|---|
| Cubicador | `POST /recomendaciones`, `GET /tipos-mercancia`, `GET /tipos-carroceria` funcionando; URL de QA; llave de Cotifly registrada; `GET /tipos-vehiculo` (P1); ejemplos del contrato (P2). Detalle en `PETICIONES_AL_CUBICADOR.md` | HU-023.1, 023.2 y 023.3 |
| NEXO | `GrupoRutaId` e `idRecomendacionVehiculo` en `clsCapturaLineaInput` y en `tbl_lineas_solicitud`; columna de orden; cuarentena de vehículos (`EsCatalogoOficial`); consumo de `/recomendaciones`. **No tienen HU escrita todavía** | HU-023.4, y la fecha del corte (HU-023.2) |
| NEXO y cubicador | Confirmar la fecha real del corte | HU-023.2 |
| Negocio | Margen promedio: opción A o B, y promedio simple o ponderado (HU-016.1, parte B); motivo de pérdida libre o de lista (HU-018.1, parte B) | HU-016.1 y HU-018.1 |

## Qué pasó con cada HU original de los pendientes de Duban

| HU original | Qué pasó | Dónde está ahora |
|---|---|---|
| 014.1 `estadoEnvio` sin `CHECK` | **Absorbida.** El `CHECK` está hecho (HU-021.2) y la tolerancia de filas malas se hace al reescribir la consulta (HU-021.4) | HU-021.2 y 021.4 |
| 014.2 Cotización de prueba de Duban | **Descartada.** La base compartida se borró y recreó | — |
| 014.3 Header `X-Portal-clsUsuario-Id` | Agrupada | HU-014.3, parte A |
| 014.4 Retirar el borrador de métricas | Agrupada | HU-014.3, parte B |
| 015.1 Catálogo de vehículo y clase de viaje | Agrupada y **cumplida**; vehículo y carrocería pasaron al cubicador | HU-015.1 |
| 015.2 Clase de contenedor | Agrupada; lo pendiente pasó a HU-023.1, parte B | HU-015.1 y HU-023.1 |
| 015.3 Servicios conexos | Agrupada y **cumplida**; guardar en tabla por ruta es HU-021.8 | HU-015.1 y HU-021.8 |
| 015.4 Línea de negocio en vivo | **Nota, no issue** | HU-015.1, nota |
| 016.1 Listado regional | Agrupada | HU-016.1, parte A |
| 017.1 Margen promedio | Agrupada | HU-016.1, parte B |
| 018.1 Campos del detalle | Agrupada; depende de #138 | HU-018.1, parte A |
| 018.2 Catálogo de referencia | **Nota, no issue** | HU-018.1, "Referencia" |
| 019.1 Ganada/perdida | Agrupada; el backend ya está, falta el frontend | HU-018.1, parte B, y HU-021-FE.11 |
| 020.1 Exportar Excel | Agrupada; reusa la exportación de licitaciones | HU-018.1, parte C, y HU-023.5 |

## Sin incluir

- El manejo del prefijo de dominio (`boterosoto\usuario`) en el mecanismo de regional (`clsDirectorioEjecutivosDimEjecutivo`, PR #83) se revisó y **se ve correcto**; no se incluye salvo que aparezca un caso concreto en pruebas reales.
- Nada sobre el envío de correo (`Correo:Habilitado`): es configuración y credenciales del relay SMTP corporativo, no una corrección de código.
- El `TrqType` de SAP para `DevolucionContenedor` lo da negocio o SAP; se anota en HU-021.2 y en `docs/backlog/2026-10-08_PENDIENTES_AL_FINALIZAR_CORRECCION_BD.md`.

## Orden sugerido

1. **HU-014.3, parte A** (commitear el header) **antes** de unir las ramas de HU-021 a QA.
2. **HU-023.1** (adaptador y catálogos) y **HU-023.2** (transición), para el viernes 16.
3. Unir HU-021 a QA. Después: **HU-023.3** (base de datos y solicitud con camiones) y **HU-023.5** (licitación).
4. **HU-016.1** y **HU-018.1**, sobre el resumen y el contrato nuevos (#138). La parte B de HU-018.1 solo necesita la decisión de negocio.
5. **HU-023.4** (envío a NEXO) cuando NEXO tenga sus cambios.
6. **HU-014.3, parte B** (retirar el borrador de métricas) cuando HU-010 esté validada en producción.
7. **HU-015.1:** se cierra con su registro en cualquier momento.
8. **HU-022.1 al final de todo.**
