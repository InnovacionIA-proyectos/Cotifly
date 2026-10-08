# ADR-019 — Reestructuración: rutas y camiones

- **Estado:** Aceptada (decisiones del usuario del 2026-10-08; el `0010` está pendiente de revisión de scalderon-bs; las extensiones quedan en #132 a #137)
- **Fecha:** 2026-10-08
- **Issue / HU:** #130 (HU-021.3), #131 (HU-021.4) · milestone HU-021 · reemplaza en parte ADR-015, ADR-017 y ADR-018

## Contexto

El `0009` (`db/migrations_mssql/0009_reestructuracion_cotizaciones.sql`, PR #128 de scalderon-bs) reestructura `dbo_cotifly` en una jerarquía **solicitud → rutas → camiones**:

| Tabla | Antes | Qué guarda |
|---|---|---|
| `CotizacionesSolicitud` | `Cotizaciones` | Cabecera: cliente, folio, versión, moneda/TRM, PDF y textos, resultado, envío, seguimiento del costeo con Nexo, `precioFinal` (suma de las rutas) |
| `CotizacionLineasSolicitud` | (líneas de ADR-017) | La ruta: origen/destino, condiciones del contenedor, horas, peso, valor de la mercancía y del levante, totales de costo, margen (`margenPoliticaSugerido`, `margenSAPSugerido`, `margenAplicado`), método y `precioFinalLinea` |
| `CotizacionVehiculosLineaSolicitud` | (nueva) | Un camión por fila: vehículo, carrocería, peso, `costoFlete`, `costoRetornoContenedor`, `costoLevanteContenedor`, id de la línea de Nexo y su estado |
| `CotizacionMercancias` | `CotizacionPosiciones` | Las mercancías de la ruta (`idTipoMercancia`, `nombreMercancia`) |
| `CotizacionServiciosConexos` | JSON `Cotizaciones.serviciosConexos` | Los conexos de la ruta: id, precio (= costo de Nexo), cantidad |

La base compartida ya se instaló con ese esquema el 2026-10-08 y el backend dejó de funcionar sobre ella. Esta ADR fija cómo lo usa el código y registra las decisiones del usuario que resuelven los choques de la propuesta con HU-005.1, ADR-018 y #124.

## Decisión

### Las 8 decisiones del usuario (2026-10-08)

1. **`referencia` y `valorEstimado` se eliminan** del dominio, la persistencia y el envío a Nexo. Mientras #132 y #138 ajustan la captura y la API, los DTO aceptan `referencia` y `valor_estimado` y los ignoran (propiedades obsoletas).
2. **Municipios: id obligatorio y copia del nombre** en todos los flujos; las ciudades de devolución y de levante pasan a id + nombre. Requiere el `0010` (abajo).
3. **Conexos tal cual la propuesta:** filas de la ruta (`CotizacionServiciosConexos`) con el costo de Nexo como precio; se envían pegados al primer camión de la ruta en Nexo. El aviso `conexos_descartados` de #124 se rehace (#134).
4. **Levante:** el comercial lo digita en la ruta (`valorLevanteContenedor`) y se **copia a cada camión** (`costoLevanteContenedor`).
5. QA espera a este núcleo; no se restaura el esquema viejo.
6. El asistente propone el margen **por ruta** y, si hay varias, pregunta cuál (#135).
7. Vehículo y carrocería **los define el cubicador o la Mesa de Costeo de Nexo**, no el comercial; las columnas del Excel se retiran (#132, #133).
8. Métricas: los **conteos son por cotización**; los **valores y la línea de negocio, por ruta** (#137).

### Agregado y reglas (§8 y §9 de `docs/database/CAMBIOS_BACKEND_REESTRUCTURACION_COTIZACIONES.md`)

- `clsCotizacionSolicitud` es la raíz del agregado; `clsLineaSolicitud` (ruta), `clsVehiculoLineaSolicitud` (camión), `clsMercancia` y `clsServicioConexoRuta` solo cambian a través de ella. EF los mapea como colecciones con campo de respaldo y borrado en cascada del lado del cliente (las FK de la base son `NO ACTION`); el repositorio carga el grafo con `AsSplitQuery`.
- **Totales guardados.** Cada cambio de camión o conexo recalcula la ruta: `costoFletesTotal`, `costoRetornoContenedorTotal`, `costoLevanteContenedorTotal`, `costoServiciosConexos` (Σ precio × cantidad) y `costoFinal` (su suma); `precioFinalLinea` = `costoFinal` con el margen y el método de la ruta (`clsCalculadoraPrecio`, en la moneda y TRM de la cabecera). `precioFinal` de la cabecera = Σ `precioFinalLinea` (NULL mientras alguna ruta no tenga precio).
- **Estado agregado** (`clsResumenEstadoCosteo`, la misma regla camión → ruta y ruta → cabecera, "el más atrasado"):
  1. sin elementos o todos NULL → NULL (chat o sin camiones);
  2. un NULL mezclado con estados cuenta como `pendiente_envio` si la solicitud **no** se ha enviado a Nexo, y como `pendiente_costeo` si ya se envió (tiene `solicitudNexoId`): así un camión sin estado no corta el polling ni permite reenviar la solicitud (regla del coordinador, 2026-10-08);
  3. `descartada` se ignora salvo que todos lo sean;
  4. si todos los que quedan coinciden, ese estado;
  5. si no, el primero de `error_envio` → `costeo_vencido` → `pendiente_envio` → `pendiente_costeo` → `entregada` → `autocosteo`.

  Las transiciones de la solicitud (envío, error de envío, vencimiento, descarte) se aplican a los camiones y la cabecera vuelve a ser el resumen de sus rutas.
- **Camiones descartados y totales (misma regla del coordinador).** Los camiones `descartada` se ignoran **en el estado y en los totales** (`TodosConFlete`, la suma de fletes, retornos y levantes, `costoFinal` y el precio), salvo que todos estén descartados: entonces cuentan todos, la ruta queda `descartada` y sin flete (`clsLineaSolicitud.VehiculosQueCuentan`).
- **Invariantes en la raíz.** Fuera del dominio nadie cambia camiones ni conexos directamente: los métodos de `clsVehiculoLineaSolicitud` (estado, costeo, vehículo, línea de Nexo) y `AgregarOActualizarConexo`/`ConservarConexos` de la ruta son `internal`. La raíz expone `AplicarFotoNexo` (lectura de Nexo por camión, `clsCosteoCamionNexo`), `AsignarLineaNexo`, `CambiarConexos` y `ConservarConexos`, que siempre resumen el estado y recalculan totales y precio (con PDF, `AplicarFotoNexo` recibe solo estados y el precio de la versión se conserva). Las pruebas usan los métodos internos con `InternalsVisibleTo`; la persistencia, solo `ReemplazarRutasLeidas` para recargar.
- **Recarga del agregado.** `RecargarAsync` limpia el rastreador, vuelve a leer el agregado completo (`ConAgregado`) y reemplaza las rutas de la misma instancia: una fila borrada por otro proceso desaparece (no se reinserta al guardar) y una nueva aparece.
- **Interbloqueos.** Leer o guardar el agregado toca varias tablas. Fuera de una transacción, la lectura del agregado, la consulta del tablero y `GuardarCambiosAsync` se repiten hasta tres veces ante el error 1205 (sobre todo el guardado después del alta en Nexo: perderlo dejaría la solicitud en Nexo sin `solicitudNexoId` y el reintento la duplicaría). Dentro de `GuardarCambiosSiVersionAsync` el 1205 sigue siendo `CONFLICTO_CONCURRENCIA`.
- **Solicitud de Nexo desconocida.** Una cotización `pendiente_costeo` (o `autocosteo`) sin `solicitudNexoId` (migrada sin respaldo) no se puede consultar: el polling (y el registro del resultado) la deja en `error_envio` con el motivo `SOLICITUD_NEXO_DESCONOCIDA` (no definitivo). Desde ahí funcionan el reintento, la re-solicitud y la perdida solo en Cotifly (#127).
- **Tarifa y conexos.** El precio de la ruta incluye los conexos. El PDF (de una ruta o de pliego), el Excel de la licitación y su vista muestran la **tarifa del transporte** y los conexos aparte, una sola vez; el total del pliego, del Excel y de la vista de la licitación es la suma de las tarifas, sin conexos. La tarifa absorbe el redondeo (`precioFinalLinea − conexos` cuando la diferencia con el transporte calculado es de a lo sumo una unidad por conexo), así tarifa + conexos = precio de la ruta, exacto.
- **Invariantes que se conservan:** `versionNegocio` y el candado de `clsUnitOfWork`, `sp_getapplock` (#125), `pdfDesactualizado` y la regla del PDF, `textosCotizacion`, resultado y "perdida" solo en Cotifly (#127), `costeoConsultasFallidas` y los reclamos del polling. `costeoUltimaConsultaAt` ya no se usa (el `0009` lo borró).
- **Margen sugerido dividido:** la ruta guarda `margenPoliticaSugerido` y `margenSAPSugerido`; el vigente es el de SAP si existe y si no el de la política. El ajuste de margen guarda la ruta en `MargenAjustes.lineaSolicitudId`.

### Envío a Nexo por camión y emparejamiento (HU-021.7, #134)

- **Una línea por camión.** `clsCotizacionSolicitud.LineasNexo()` arma, ruta por ruta y en el orden estable de los camiones (`createdAt`, luego `id`), una línea de `CapturaLineaRequest` por camión: de la ruta, el origen y el destino (id del municipio y el nombre guardado), la tipología, la clase de viaje, las condiciones del contenedor, el valor de la mercancía, el levante (la copia del camión) y las horas; del camión, el vehículo y la carrocería (vacíos si no hay: la línea viaja igual y la resuelve la Mesa, la solicitud nunca se parte) y su peso. Las posiciones solo viajan con un único camión en la ruta (hasta #133).
- **Campo de ruta (N1).** Nexo no tiene en `CapturaLineaRequest` un campo para la ruta de Cotifly ni uno de texto libre por línea (no hay `notas` por línea), y su swagger declara `additionalProperties: false`. Por eso: (1) cada línea lleva internamente el id de su ruta (`clsLineaSolicitudCosteo.RutaId`), que viaja como `referenciaRuta` **solo** con `Nexo:EnviarReferenciaRuta = true` (apagado por defecto; se enciende cuando Nexo lo acepte); (2) si Nexo devuelve `referenciaRuta` en `GET /api/solicitudes/{id}`, el emparejador la usa como llave de la ruta; (3) mientras tanto, cuando alguna ruta viaja partida en varios camiones, `notas` del encabezado le dice a la Mesa cuántas líneas son de cada ruta (`"Cotifly: una línea por camión. Ruta 1 (Buenaventura → Bogotá D.C.): 3 camiones."`, máximo 500 caracteres; el resto es del comercial). Se propone `referenciaRuta` y no `rutaId` porque `LineaCosteoDto.rutaId` ya existe en Nexo y es su catálogo de rutas origen-destino.
- **Emparejamiento (`clsEmparejadorLineasNexo`).** Nexo devuelve las líneas sin orden. La llave es la ruta (la referencia N1 si ambos lados la traen; si no, `"Origen → Destino"` normalizado) y, cuando ambos lados lo traen, el peso, la tipología, el vehículo y la carrocería. Un dato que solo trae un lado no contradice (la Mesa pudo ponerle vehículo a una línea vacía). Primero se asocian los pares únicos en ambos sentidos. **Empates:** los camiones idénticos de la misma ruta son intercambiables (valen lo mismo en Nexo); si sus líneas candidatas no las puede reclamar otro camión, se asignan en orden estable (los camiones en el orden de envío y las líneas por su id de Nexo, en orden ordinal) y se guarda `lineaSolicitudNexoId` en cada uno. Sin N1 queda un riesgo: que el **estado** de dos camiones idénticos se cruce (el costo no). Quedan sin asociar, con motivo y sin precio: los camiones iguales de rutas distintas (pueden costar distinto por datos que no viajan en la línea), los datos que se contradicen y los empates que otro camión puede reclamar.
- **Lectura del swagger.** El swagger de Nexo QA (2026-10-08) publica la ruta de `LineaCosteoDto` como `clsRuta` en vez de `ruta` (renombre de su DTO). Cotifly lee los dos nombres; sin ninguno, solo empareja con la referencia N1 o cuando hay un camión y una línea.
- **Polling.** Por cada camión vigente registra el flete, el retorno y el estado (`AplicarFotoNexo`, que resume las rutas y recalcula totales y precio), y copia el vehículo y la carrocería que puso la Mesa si el camión iba vacío. Los camiones `descartada` no se leen, no consumen una línea de Nexo y no cuentan como "sin asociar" (`clsPrecioCosteoService.Vigentes`). El guardado se repite ante el error 1205 (regla del núcleo).
- **Conexos (decisión 3).** Se envían con `POST /api/cotizaciones/{id}/conexos` y el `lineaId` de la línea de la cotización de Nexo del **primer camión** de la ruta, después del costeo, con la versión reservada y la `Idempotency-Key` de #99. Al leerlos de vuelta, cada conexo va a la ruta del camión cuya línea lo lleva (`cotizacionLineaId` → `lineaSolicitudId` → camión → ruta; si la cadena no se puede seguir, a la ruta del pedido o a la ruta 1). El polling sigue sin importar conexos, salvo con la marca de #124.
- Se eliminaron `clsServicioConexoCosteo` y `clsServiciosConexosJson`: el JSON de conexos ya no existe.

### Script `0010_nombres_municipio.sql` (borrador, pendiente de revisión de scalderon-bs)

Idempotente, no destructivo y en una transacción. Agrega a la ruta `origenNombre`, `destinoNombre`, `ciudadDevolucionNombre` y `ciudadLevanteNombre` (los ids de devolución y levante ya los trae el `0009`; se agregan solo si faltan), y además:

- `CotizacionesSolicitud.solicitudNexoId`: el `0009` borró `costeoId` como "nunca usado", pero es el id de la **solicitud** de Nexo que devuelve el alta y consulta el polling (`cotizacionNexoId` es el de la cotización de Nexo). Sin él no hay polling.
- `CotizacionServiciosConexos.nombreServicioConexo`: copia del nombre del conexo para el PDF (mismo criterio de la decisión 2).
- `ck_cotizaciones_estadoEnvio` si falta (la base compartida se instaló antes de que el `0009` lo trajera).
- Si existen los respaldos `Respaldo0009_*`, llena desde ellos los nombres y `solicitudNexoId` vacíos, incluidos los nombres de los conexos que venían del JSON de cada línea de una licitación (`Respaldo0009_CotizacionLineasSolicitud.serviciosConexos`).

`db/scripts/01_crear_esquema.sql` ya trae esas columnas; una prueba de integración verifica que la instalación desde cero y las migraciones `0001` → `0010` dejan el mismo esquema.

### Sonda `/health/ready`

`clsBaseDatosHealthCheck` se rehízo desde el modelo EF: compara las tablas y columnas mapeadas con la base y verifica también `ck_cotizaciones_estadoEnvio`. Responde `falta 0002` (sin las tablas de usuarios), `falta 0009` (esquema anterior: no hay `CotizacionesSolicitud`), `falta 0010` (faltan las columnas del `0010` o el `CHECK` de `estadoEnvio`) o `falta <Tabla.columna>` para cualquier otra diferencia.

### Qué reemplaza

- **ADR-015** (costeo en Nexo): el id de la solicitud pasa de `Cotizaciones.costeoId` a `CotizacionesSolicitud.solicitudNexoId`; el costo, el margen y el precio son de la ruta; el estado se guarda en el camión y se resume en ruta y cabecera; `valorEstimado` y `referencia` ya no viajan a Nexo.
- **ADR-017** (licitaciones): cada fila del Excel es una ruta con un camión; las columnas de línea de la cabecera (`origen`, `destino`, `costo`, `lineaNegocioId`…) ya no existen; `filaOrigen` es `rutaOrigenIndice`.
- **ADR-018**: §3 (margen) pasa a ser por ruta; §12 (conexos) deja el JSON y usa filas de la ruta; la tabla de idempotencia irá en el siguiente script libre (hoy el `0011`).

## Adaptaciones mínimas (las extensiones van en #132 a #137)

| Adaptación | Issue que la completa |
|---|---|
| Una ruta con **un** camión en el formulario y el chat; N rutas con un camión en las licitaciones. Vehículo y carrocería siguen saliendo del formulario | #132, #133 (cubicador) |
| Las posiciones solo viajan a Nexo cuando la ruta tiene un único camión | #133 |
| ~~El emparejamiento con la foto de Nexo deja sin asociar los camiones empatados~~ **Resuelto en #134:** los idénticos de una ruta se asignan en orden estable (sección anterior) | #134 |
| El polling no importa los conexos de Nexo (la fuente es Cotifly, decisión 3), salvo con la marca de "agregado sin relectura" de #124, que ahora los lleva a la ruta de su camión | #134 |
| `referenciaRuta` (N1) no viaja hasta que Nexo la acepte (`Nexo:EnviarReferenciaRuta`) | Nexo (N1) |
| Excluir un conexo lo **borra** de la ruta (ya no hay columna `incluido`); para volver a incluirlo se agrega de nuevo | #135 |
| El ajuste de margen aplica el mismo margen a todas las rutas; `MargenAjustes.lineaSolicitudId` = ruta 1 | #135 |
| El precio de la ruta (y el de la vista de costeo) incluye los conexos; el PDF y el Excel imprimen la tarifa del transporte y los conexos por separado, y el total del pliego sigue sin conexos | #136 |
| La re-solicitud en el mismo folio descarta los conexos (la captura nueva de Nexo empieza sin ellos; se avisan en `conexos_descartados`) | #136 |
| El chat no guarda municipios (ni id ni nombre): su ruta queda sin ubicación (decisión 2) | #132 |
| `IdTipoMercancia` no se exige en la captura vieja (`clsMercancia.DesdeCaptura`) | #132 |
| Los ids de las ciudades de devolución y levante no se resuelven ni se guardan (solo el nombre) | #132 |
| Cotizaciones migradas por el 0009 cuyo `precioFinalLinea` no incluye los conexos: se recalculan con el próximo cambio de costo, conexos o margen (mientras tanto el PDF usa el transporte calculado) | #136 |
| Métricas por cotización: costo = Σ `costoFinal` de las rutas y margen ponderado por costo; la ciudad es la de la ruta 1 | #137 |
| La API acepta `valor_estimado` y `referencia` y los ignora | #138 |

## Alternativas consideradas

- **Mantener las columnas de línea en la cabecera como espejo de la ruta 1:** descartada; el `0009` las borró y duplicarían la fuente de verdad.
- **Guardar el estado solo en la cabecera:** descartada; con varios camiones Nexo costea cada uno por separado.
- **Seguir con `costeoId` en `cotizacionNexoId`:** descartada; son dos ids distintos de Nexo (solicitud y cotización).

## Consecuencias

- El backend vuelve a funcionar sobre `0001` → `0009` + `0010` (o `01_crear_esquema.sql`). **Sin el `0010`, `/health/ready` responde `503 falta 0010`**: hay que aplicarlo antes de desplegar.
- Los costeos guardados antes del `0009` sin respaldos no tienen `solicitudNexoId`: el polling no los puede consultar y los deja en `error_envio` con `SOLICITUD_NEXO_DESCONOCIDA` (reintento, re-solicitud o perdida solo en Cotifly).
- El precio de la cotización incluye los conexos (antes se sumaban aparte en la vista). `openapi.yaml` y la guía del frontend lo avisan: el frontend no debe sumarle el precio de los conexos.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Revisión y aplicación del `0010` en la base compartida (después del `0009`, antes de desplegar) | scalderon-bs y Duban |
| Extensiones de la tabla de adaptaciones | #132 a #137 |
| Contrato de la API y `openapi.yaml` | #138 |
| N1 (`referenciaRuta` en `CapturaLineaRequest` y en `LineaCosteoDto`) y aclarar el renombre `ruta` → `clsRuta` del swagger | Equipo de Nexo (preguntas 18 a 20 de `docs/conexion_nexo/HU-013-preguntas-nexo.md`) |
