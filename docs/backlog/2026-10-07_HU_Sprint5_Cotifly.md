# Sprint 5 — Cotifly (HUs de backend y cierre)

> **Fecha de redacción:** 2026-10-07. **Versión final consolidada:** 2026-10-08.
> **Fuente:** `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md` (hallazgos de pruebas en vivo, 2026-09-29 a 2026-10-05) más la deuda técnica detectada aparte.
> **Convención:** mismo formato de `docs/backlog/2026-09-21_HU_Backlog_Cotifly_ClientePro.md`. Numeración desde HU-014 (la de `ADR-018` es HU-013).
> **Solo backend:** este documento no tiene HU de frontend. Lo que el Sprint 5 necesita del frontend está en `docs/backlog/2026-10-08_HU_Correccion_BD_Frontend_Cotifly.md`.

**El Sprint 5 va DESPUÉS de `docs/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md` (HU-021, "[Corrección con BD]").** El 2026-10-08 se recreó `dbo_cotifly` con el esquema nuevo (solicitud → rutas → camiones) y el backend actual no funciona contra él hasta terminar esa HU.

---

## Resumen: 5 issues de backend (antes 14)

Los 14 issues originales se agruparon por tema para no abrir tantos. La numeración conserva la del primer miembro de cada grupo.

| Issue | Repo | Reúne | Depende de |
|---|---|---|---|
| **[HU-014.3]** Deuda técnica de backend: header definitivo y retiro del borrador de métricas | `Cotifly` | antes 014.3 y 014.4 | HU-010 validada; coordinar con HU-021.7 y 021.10 |
| **[HU-015.1]** Catálogos y servicios conexos de Nexo: verificar y cerrar | `Cotifly` | antes 015.1, 015.2 y 015.3 | HU-021.8 |
| **[HU-016.1]** Gerente Comercial: listado regional y margen promedio | `Cotifly` | antes 016.1 y 017.1 | HU-021.4, 021.9, 021.10 |
| **[HU-018.1]** "Ver detalle": campos nuevos, ganada/perdida y exportar Excel | `Cotifly` | antes 018.1, 019.1 y 020.1 | HU-021.9, 021.11, 021.13 |
| **[HU-022.1]** Cierre: limpiar el código viejo y dejar la documentación profesional | `Cotifly` | — | **al final de todo** |

**Ya no son issues** (se muestran abajo, en "Qué pasó con cada HU original"): HU-014.1 (absorbida en HU-021.2 y 021.4), HU-014.2 (descartada), HU-015.4 y HU-018.2 (notas).

---

## HU-014 — Deuda técnica de backend

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)

### [HU-014.3] Deuda técnica de backend: header definitivo y retiro del borrador de métricas
**Labels:** `backend`, `tarea-tecnica`, `deuda-tecnica`
**Reúne** las antiguas HU-014.3 y HU-014.4. Son dos tareas pequeñas e independientes: se pueden hacer en cualquier orden, pero la primera va **antes de HU-021.7**.

**Parte A — Commitear como definitivo el header `X-Portal-clsUsuario-Id`.**
Decisión (2026-10-05): Nexo no va a corregir el nombre del header de su lado (bug admitido en `CONTRATO.md §7`, discrepancia #1). El ajuste queda **permanente** en Cotifly.

- [ ] El ajuste (`"X-Portal-Usuario-Id"` → `"X-Portal-clsUsuario-Id"`) ya está aplicado y los comentarios ya no dicen "revertir". **Son 6 archivos** (a 2026-10-08): `clsBuscadorClientesNexo.cs`, `clsCatalogosMaestrosNexo.cs`, `clsCosteoNexo.cs` (dos sitios), `clsCubicacionNexo.cs`, `clsMunicipiosNexo.cs` y `clsAnalizadorExcelNexo.cs`.
- [ ] **Falta commitearlos como definitivos.** Siguen sin commit a propósito, para que los commitee Duban. Ojo con `clsCosteoNexo.cs`: HU-021.7 lo reescribe, así que conviene commitear esto **antes** para no mezclar los dos cambios.
- [ ] Cuando el cubicador entre (HU-021.5), su adaptador **no** usa este header: autentica con `Authorization: Bearer <CUBICADOR_API_KEY>`.
- [ ] Al comitear, aprovechar el mismo commit para corregir las rutas de documentos viejas dentro de `backend-dotnet/` (punto 11 de los pendientes de Duban, 28 archivos).

**Parte B — Retirar el borrador obsoleto del tablero de métricas (`/actividad`, `/ajustes-margen`).**
**Depende de que HU-010 (tablero real, `/web/metricas/comercial` y `/regional`) esté validado en producción sin depender de esto.** Estos dos endpoints fueron el primer borrador del tablero; ya están marcados `(obsoleto)` en el código (`clsMetricasController.cs`) y `clientepro-frontend` no los consume.

- [ ] Confirmar que ningún consumidor externo (Postman guardado, script, otro servicio) sigue llamándolos.
- [ ] Retirar los endpoints `Actividad` y `AjustesMargen` de `clsMetricasController.cs`.
- [ ] Retirar `ListarActividadAsync` y `ListarAjustesMargenAsync` de `IClsMetricasRepository`/`clsMetricasRepository.cs`, y los DTOs `clsActividadComercial` y `clsAjustesMargenComercial`.
- [ ] Retirar las vistas `vActividadPorComercial` y `vAjustesMargenPorComercial` con un script nuevo **`0010_retirar_vistas_actividad.sql`**, y quitarlas también de `db/scripts/01_crear_esquema.sql` (regla: toda migración nueva se refleja en el 01). El `0009` queda como respaldo y no se toca. Si esta tarea se hace **antes** de HU-021.10, se quitan en vez de adaptarlas.
- [ ] Actualizar `docs/docs_front/CONTRATO_API_WEB.md` y `specs/openapi.yaml` quitando ambas rutas.

---

## HU-015 — Catálogos y servicios conexos: usar las fuentes correctas de Nexo

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)

### [HU-015.1] Catálogos y servicios conexos de Nexo: verificar y cerrar
**Labels:** `backend`, `integración`
**Reúne** las antiguas HU-015.1, 015.2 y 015.3. **Es un issue de verificación primero**: una revisión del código (2026-10-08) encontró que buena parte ya está hecha, y el pull de QA del 2026-10-07 trajo HU-013.3 y HU-013.10 (#106).

**Contexto original (2026-10-05):** Cotifly no usaba las fuentes correctas de Nexo para que la información del formulario se cree en sus tablas: traía tipo de vehículo y clase de viaje de `GET /api/biblioteca-sap-historico/filtros` (pensado para filtrar histórico), usaba un catálogo propio de clase de contenedor, y no enviaba a Nexo la selección de conexos del comercial.

**Lo que ya existe en el código (verificado el 2026-10-08, sin confirmar que cubra todo):**
`clsCatalogosMaestrosNexo` y su DTO (`/api/catalogos/tipos-vehiculo`, `/clases-viaje`, `/clases-contenedor`, `/lineas-negocio`), `clsServiciosConexosNexo` y `clsServiciosConexosController` (catálogo `GET /api/servicios-conexos`).

- [ ] **Verificar y cerrar tipo de vehículo y clase de viaje.** Con el cubicador (HU-021.5) el comercial **ya no escoge** vehículo ni carrocería: salen de la recomendación y se guardan por camión. Solo hace falta la **clase de viaje** (es de la ruta), contra `GET /api/catalogos/clases-viaje`. Vehículo y carrocería quedan para el acuerdo de códigos entre Nexo y el cubicador (en Nexo el vehículo es una sola combinación del catálogo: tipo + carrocería + rango de peso, `tbl_configuraciones_vehiculo`). La pregunta de si existe un catálogo de **carrocería** en Nexo se cierra con eso.
- [ ] **Clase de contenedor.** Confirmar que el backend ya valida contra `GET /api/catalogos/clases-contenedor` y que no queda el catálogo propio hardcodeado (`ClaseContenedor.cs`). Si queda, quitarlo.
- [ ] **Servicios conexos: enviar la selección a Nexo.** Verificar si ya existe `POST /api/cotizaciones/{id}/conexos` (`Roles: Comercial, CoordinadorFletes, Admin`; body `{ items: [{ servicioConexoId, cantidad?, valorAplicado? }] }`). Sin esto, la elección del comercial queda solo en Cotifly y la cotización real en Nexo sale sin conexos (afecta reportes, biblioteca de tarifas ganadas y auditoría de Nexo).
- [ ] **Cómo se guardan los conexos en Cotifly** cambia (tabla por ruta, no JSON en la cabecera) y es parte de **HU-021.8**, no de este issue. Aquí queda lo que ya estaba: el catálogo y **enviar la selección a Nexo**. Como Nexo guarda los conexos pegados a una línea (para él, un camión), los de una ruta se envían pegados a la línea del **primer camión** de esa ruta; ese mismo punto está en HU-021.8, así que **se hace una sola vez, allá**.
- [ ] Si todo lo anterior ya estaba hecho, **cerrar el issue** dejando anotado qué se verificó.

---

## HU-016 — Gerente Comercial: listado regional y margen promedio

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)

### [HU-016.1] Gerente Comercial: listado regional y margen promedio
**Labels:** `backend`, `producto`
**Reúne** las antiguas HU-016.1 y HU-017.1. Las dos tocan lo que ve el comercial y su gerente en el tablero y el listado, y las dos se construyen sobre el resumen nuevo de HU-021.

**Parte A — Endpoint `GET /web/cotizaciones/regional`.**
La vista de Gerente Comercial "Todas las cotizaciones" necesita un listado fila por fila de las cotizaciones de los comerciales de su regional. Hoy no existe: `GET /web/mis-cotizaciones` solo trae las del propio usuario y `GET /web/metricas/regional` es agregado.

- [ ] Construir `GET /web/cotizaciones/regional` (mismo patrón de nombre y política `GerenteComercial` que `/web/metricas/regional`).
- [ ] Reusar `clsRegionalUsuarioService.ListarMiembrosAsync(regional)` y un método nuevo en `IClsCotizacionRepository` (`ListarPorUsuariosAsync(IReadOnlyCollection<Guid> usuarioIds, ...)`) que filtre `WHERE usuarioId IN (...)`; no reconstruir la resolución de regional.
- [ ] Se construye **después de HU-021.4 y 021.9**, sobre el repositorio y el resumen nuevos. La línea de negocio de una fila sale de la ruta 1 de la solicitud (o "Varias"), y el precio es el `precioFinal` de la cabecera.

**Parte B — "Margen promedio" en "Mis cotizaciones": contra qué se compara.** *(Pendiente de decisión de negocio antes de construir.)*
La tarjeta muestra un "± desviación" que casi siempre sale vacío (`—`). No es un dato que falte: `DesviacionPromedioMargen` (`clsCalculadoraMetricas.cs`) promedia `MargenAjustes.diferencia`, tabla que solo se escribe cuando el comercial **negocia** un margen distinto (documentado así en ADR-016).

- [ ] **Lo que negocio quiere** (pedido explícito de Sara, 2026-10-05): comparar el margen aplicado promedio contra el **promedio de los márgenes de política de todas las líneas** (`MargenesPolitica.margen`). Verde si está por encima, rojo si está por debajo.
- [ ] **Opción A (más segura):** endpoint nuevo accesible para Comercial que liste `MargenesPolitica` (línea y margen) de las líneas activas, para que el cliente calcule el promedio. Hoy `GET /web/admin/lineas-negocio` exige la política `GerenteComercial`; no hay ADR que declare esos márgenes confidenciales para el Comercial (ya se filtran uno por uno por el chat, herramienta `obtener_margen`).
- [ ] **Opción B (más riesgosa):** cambiar la definición de `DesviacionPromedioMargen`. ADR-016 la define a propósito como "solo ajustes negociados"; podría afectar otros reportes.
- [ ] **Decidir con negocio cuál opción antes de construir.**
- [ ] **Con el esquema nuevo:** el margen aplicado ahora es **por ruta** (`CotizacionLineasSolicitud.margenAplicado`) y los ajustes también (`MargenAjustes.lineaSolicitudId`). El margen promedio de un comercial se calcula sobre sus rutas y no sobre sus cotizaciones. **Si es promedio simple o ponderado por costo no está decidido**: el pendiente original hablaba de un promedio simple y HU-021.10 recomienda ponderar por costo. Confirmar con negocio antes de construir.

---

## HU-018 — "Ver detalle" de una cotización

> **Repo:** `Cotifly` · **Asignado a:** Duban (Backend)

### [HU-018.1] "Ver detalle": campos nuevos, ganada/perdida y exportar Excel
**Labels:** `backend`, `producto`, `investigacion`
**Reúne** las antiguas HU-018.1, 019.1 y 020.1. Las tres son lo que se ve y se hace desde "Ver detalle". **Se construye sobre HU-021.9 y HU-021.11** (el DTO se reescribe allá) y sobre HU-021.13.

**Parte A — Campos del detalle y desglose del costeo.**
- [ ] **Los campos del detalle ya no se agregan al DTO viejo.** El DTO `clsCotizacionResumen` (que no traía `deadline`, `origen`, `destino`, `pesoKg` ni `valorMercancia`) se reescribe en **HU-021.9 y HU-021.11**: `origen`, `destino`, `pesoKg` y `valorMercancia` pasan a ser de cada **ruta** (origen y destino como id de municipio, resueltos a nombre con el catálogo de Nexo) y solo `deadline` sigue en la cabecera. Esto **se hace allá**; aquí solo se verifica que el detalle expone `deadline` y el resumen de las rutas (origen → destino, peso, valor y camiones).
- [ ] Capturar `scorePrioridad` cuando Nexo responde al **crear** la solicitud (`POST /api/captura-solicitudes` → `CapturaResultDto`), que hoy se descarta (`clsSolicitudCreadaNexo`). Es una ganancia pequeña y ya desbloqueada. Consultarlo después por solicitud (`scoreUrgencia`) sigue **bloqueado** por el contrato de Nexo (ADR-016).
- [ ] **Investigar con Duban** si Nexo ya manda el desglose del costeo (piso SICE-TAC con horas y vigencia, 3 fuentes candidatas lado a lado, propuesta final con su justificación, tarifas históricas de la ruta) y Cotifly lo descarta, o si hay que pedírselo aparte. Hoy Cotifly solo recibe un precio final. **No construir de una vez**: priorizar con negocio (ver "Referencia" abajo).

**Parte B — Ganada/perdida: motivo + comentario separados.** *(Pendiente de decisión de negocio.)*
`POST /web/cotizaciones/{folio}/seguimiento` funciona, pero su cuerpo es `{ ganada, motivo_rechazo }`, un solo texto libre. La HU-004.5 original pedía dos campos: **motivo** (lista predefinida, se guarda en Cotifly y se envía a Nexo) y **comentario** (texto libre, solo a Nexo). Nunca se terminó.

- [ ] **Verificar antes de construir a qué endpoint llama el frontend.** Para las cotizaciones del flujo de Nexo, `/seguimiento` las rechaza con `USAR_RESULTADO_NEXO` (`clsSeguimientoService.cs:46-52`) y el camino correcto es `POST /web/cotizaciones/{folio}/resultado` (`clsResultadoCotizacionService.cs`), que primero llama a Nexo y solo guarda local si Nexo responde 200. El frontend marca con `/seguimiento` (`detalle-cotizacion.component.ts`). Si de verdad llama a `/seguimiento`, el botón ya falla para esas cotizaciones y es un **bug aparte, anterior a esto**. Esta parte se construye sobre `/resultado`.
- [ ] `motivoRechazo` debe quedar en "No aplica" cuando se marca ganada (regla de la propuesta original, **HU-021.13**).
- [ ] **Decidir con negocio:** ¿se construyen motivo y comentario separados, o basta el texto libre? La lista de Nexo (`mesa-costeo-page.component.ts`): `Valor muy alto`, `Flete fuera de rango de plaza`, `Cliente desistió del servicio`, `Tiempo de respuesta vencido`, `Otro`.
- [ ] Si se construye: agregar `comentario` a `SeguimientoWebHttp` (backend).

**Parte C — Exportar Excel.**
El botón "Exportar Excel" de "Ver detalle" sigue deshabilitado ("Próximamente"), sin endpoint. Nexo sí lo tiene funcional en su "Mesa de Costeo", pero no se investigó qué exporta.

- [ ] Investigar qué exporta Nexo y contra qué endpoint, antes de construir el lado de Cotifly.
- [ ] Definir el contrato: ¿la misma información del PDF, un desglose, algo más? Con el esquema nuevo el desglose es **por ruta, con sus camiones**.
- [ ] **Reusar la exportación de licitaciones** (`clsLicitacionService`, que HU-021.6 rehace para el esquema nuevo) antes de construir una nueva.

**Referencia (no construir de una vez; priorizar con negocio): lo que la "Mesa de Costeo" de Nexo ya muestra y Cotifly no.**
- *Nivel solicitud:* cliente, tipo, modalidad, deadline, valor estimado, probabilidad de cierre, `scorePrioridad`, estado, fecha de captura, notas.
- *Nivel línea:* ruta, tipología, peso, vehículo, carrocería, posiciones. En el esquema nuevo se reparte: la **ruta** tiene origen, destino, tipología, peso y mercancías; el **camión** tiene vehículo, carrocería y los costos de Nexo.
- *Transparencia del costeo:* ver el desglose de la Parte A.
- *Servicios conexos:* lista con etiqueta de alcance (General / en N de M líneas / Individual); cubierto por HU-015.1 y HU-021.8.
- *Cubicaje y vehículo sugerido:* un resumen compacto basta; el desglose 3D es una herramienta de trabajo aparte.
- *Auditoría de quién hizo qué:* tampoco existe en el propio Nexo; si se quiere en Cotifly, hay que diseñarla desde cero.

---

## HU-022 — Cierre del proyecto: limpieza y documentación profesional

> **Repo:** `Cotifly` · **Asignado a:** Sara (Datos) + Duban (Backend) · **Va al final de todo el Sprint 5**, después de todas las HU anteriores (HU-021 incluida).

### [HU-022.1] Limpiar el código viejo y dejar la documentación profesional

**Contexto:** al borrar Alembic (HU-021.2) apareció que el stack viejo en Python y su infraestructura de Postgres siguen en el repo sin usarse (punto 12 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`). Además, buena parte de la documentación describe ese stack o el esquema anterior. HU-021.14 pone los documentos al día con el esquema nuevo; esta HU los deja **profesionales** y quita lo que sobra.

- [ ] **Evaluar qué quedó viejo y sin uso** (Python viejo, Alembic, Postgres, servicios de compose, scripts y pruebas del stack viejo) con un inventario: qué existe, quién lo usa y si está en algún ambiente.
- [ ] **Borrar por partes** y comprobar después de cada una que el backend compila, las pruebas pasan y el despliegue sigue sano.
- [ ] **Inventario de documentos** (`docs/`, `backend-dotnet/docs/`, `specs/`, `README.md`, `BASE_DE_DATOS.md`, `DEPLOY.md`, `infra/`, `db/README.md`): marcar cada uno como *vigente*, *por corregir* o *obsoleto*.
- [ ] **Borrar lo obsoleto**: documentos del stack viejo o propuestas ya ejecutadas y superadas. Antes de borrar, confirmar que nada vigente los enlaza.
- [ ] **Un solo lugar por tema** (esquema, API, despliegue, integración con Nexo y el cubicador, backlog) y un índice claro en `docs/README.md` y en el `README.md` de la raíz.
- [ ] **Estilo uniforme**: mismos encabezados, fecha y dueño, idioma y tono; sin texto de trabajo, tachados ni notas de sesión.
- [ ] **HU y notas de trabajo** (`docs/backlog/`): dejar las vigentes y archivar o borrar las cerradas.
- [ ] Revisar que **todos los enlaces y rutas** funcionen (incluye las 28 referencias de `backend-dotnet/`, si no se corrigieron ya en HU-014.3).
- [ ] Revisión final por alguien que **no** haya escrito el documento: ¿se entiende sin contexto?

---

## Qué pasó con cada HU original

| HU original | Qué pasó | Dónde está ahora |
|---|---|---|
| 014.1 `estadoEnvio` sin `CHECK` | **Absorbida.** El `CHECK` está hecho (HU-021.2) y la tolerancia de filas malas se hace al reescribir la consulta (HU-021.4) | HU-021.2 y 021.4 |
| 014.2 Cotización de prueba de Duban | **Descartada.** La base compartida se borró y recreó | — |
| 014.3 Header `X-Portal-clsUsuario-Id` | Agrupada | HU-014.3, parte A |
| 014.4 Retirar el borrador de métricas | Agrupada | HU-014.3, parte B |
| 015.1 Catálogo de vehículo y clase de viaje | Agrupada, con alcance reducido a clase de viaje | HU-015.1 |
| 015.2 Clase de contenedor | Agrupada | HU-015.1 |
| 015.3 Servicios conexos | Agrupada; guardar en tabla por ruta es HU-021.8 | HU-015.1 y HU-021.8 |
| 015.4 Línea de negocio en vivo | **Nota, no issue.** Cotifly usa `MargenesPolitica` con un mapeo manual al código de Nexo (`LineaNegocioNexo`); está bien. Preguntarle a Duban si conviene consumir `GET /api/catalogos/lineas-negocio` en vivo, es mantenimiento | — |
| 016.1 Listado regional | Agrupada | HU-016.1, parte A |
| 017.1 Margen promedio | Agrupada | HU-016.1, parte B |
| 018.1 Campos del detalle | Agrupada; el DTO se reescribe en HU-021.9 y 021.11 | HU-018.1, parte A |
| 018.2 Catálogo de referencia | **Nota, no issue** | HU-018.1, "Referencia" |
| 019.1 Ganada/perdida | Agrupada | HU-018.1, parte B |
| 020.1 Exportar Excel | Agrupada | HU-018.1, parte C |

## Sin incluir

- El manejo del prefijo de dominio (`boterosoto\usuario`) en el mecanismo de regional (`clsDirectorioEjecutivosDimEjecutivo`, PR #83) se revisó y **se ve correcto**; no se incluye salvo que aparezca un caso concreto en pruebas reales.
- Nada sobre el envío de correo (`Correo:Habilitado`): es configuración y credenciales del relay SMTP corporativo, no una corrección de código.

## Orden sugerido

1. **Primero HU-021 ([Corrección con BD])**, completa o al menos hasta HU-021.4: sin eso no hay backend funcionando.
2. **HU-014.3, parte A** (commitear el header) **antes** de HU-021.7, para no mezclar dos cambios en `clsCosteoNexo.cs`.
3. **HU-015.1** (verificar y cerrar) puede hacerse en cualquier momento después de HU-021.4; el envío de conexos a Nexo va junto con HU-021.8.
4. **HU-016.1** y **HU-018.1**, que dependen del resumen y los DTO nuevos (HU-021.9 y 021.11). La verificación de `/seguimiento` contra `/resultado` (HU-018.1, parte B) se puede adelantar: es independiente de la estructura.
5. **HU-014.3, parte B** (retirar el borrador de métricas) después de HU-010 validada; si va antes de HU-021.10, se quitan las vistas en vez de adaptarlas.
6. **HU-022.1 al final de todo**: la limpieza del código viejo y la documentación profesional se hacen cuando el resto esté terminado.

## Pendiente de agregar

Los acuerdos con el equipo del cubicador y con Nexo (campo de ruta, códigos de vehículo, `TrqType` de `DevolucionContenedor`) están en `docs/conexion_nexo/2026-10-08_CONSIDERACIONES_PARA_NEXO.md` (no son issues) y, para el `TrqType`, en HU-021.2 de `docs/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md`.
