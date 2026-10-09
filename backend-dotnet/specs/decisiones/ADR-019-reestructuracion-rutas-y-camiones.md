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

### Captura por rutas (HU-021.6, #132)

- **Formulario por rutas.** `POST /web/solicitudes-costeo` recibe `rutas[]` (`RutaCapturaWeb`, hasta 100): cada ruta con su línea de negocio (o la de la solicitud), origen y destino, condiciones del contenedor, ciudades de devolución y de levante, horas, `valor_levante_contenedor`, valor de la mercancía, peso, mercancías y conexos. Sin `rutas`, los campos de la raíz se leen como una sola ruta (formato plano anterior, para no romper al frontend mientras #138 cierra el contrato); mandar las dos cosas es `SOLICITUD_COSTEO_INVALIDA`. La licitación sigue siendo una fila por ruta y gana los mismos campos (`linea_negocio` por fila, ids de las ciudades, `mercancias`, `servicios_conexos`, `valor_levante_contenedor`).
- **Una sola validación para los dos flujos** (`clsCapturaRutasService`, sobre `clsValidadorFilasLicitacion`): límites de Nexo y G3 (`clsLineaSolicitudCosteo.Errores`), códigos de catálogo (G2), municipios y las reglas de captura nueva (`clsLineaSolicitudCosteo.ErroresCaptura`: id de municipio de origen y destino, id de una ciudad del contenedor que trae nombre, `id_tipo_mercancia` de cada mercancía). **Todos los errores juntos**: el formulario responde `SOLICITUD_COSTEO_INVALIDA` con `detalle.errores[]` (`ruta`, `campo`, `codigo`, `mensaje`) y la licitación su `ReporteLicitacionInvalida`. Al revalidar lo guardado (reintento, re-solicitud) no se aplican las reglas de captura nueva: las rutas migradas no tienen esos ids.
- **Municipios (decisión 2).** En una captura nueva `clsMunicipiosLicitacion` trata origen, destino y las dos ciudades del contenedor con el catálogo con caché de `GET /web/municipios` y `clsResolvedorMunicipios`: con id, el id debe existir (`MUNICIPIO_INEXISTENTE` si no) y **el nombre siempre se copia del catálogo** (un nombre distinto del cliente se ignora y queda en el log); sin id, el nombre da el id si hay una coincidencia única no aproximada (y se guarda el nombre del catálogo); si no, `MUNICIPIO_AMBIGUO` con los candidatos o `MUNICIPIO_NO_ENCONTRADO`. Nunca adivina. Toda captura consulta el catálogo; sin él, 503 `MUNICIPIOS_NO_DISPONIBLE` y nada se guarda. Al revalidar lo guardado (reintento, re-solicitud) solo se resuelven el origen y el destino sin id, también en un **formulario** migrado (si no se puede, `SOLICITUD_COSTEO_INVALIDA` con `detalle.errores` y nada viaja a Nexo). `clsLineaSolicitud.Desde`/`ALinea` copian ahora `ciudadDevolucionMunicipioId` y `ciudadLevanteMunicipioId`.
- **Varias líneas de negocio** en una solicitud se aceptan. Hasta integrar #134 (que manda la línea por camión), a Nexo viaja la de la ruta 1; el margen sí es el de cada ruta.
- **Vehículo y carrocería (decisión 7)** salieron de la captura y del mapeo del Excel: si llegan, `clsEnvioCosteoService.SinVehiculo` los borra y deja un aviso en el log (`tipo_vehiculo`/`carroceria` quedan como propiedades obsoletas en los DTO). `valor_estimado` y `referencia` (decisión 1) se siguen aceptando e ignorando. La re-solicitud (`datos_carga`) conserva el cambio manual de vehículo (es de #136).
- **Camiones: punto de extensión `IClsPlanificadorCamiones`.** Mientras no exista el cubicador, `clsPlanificadorCamionSinVehiculo` crea **un camión sin vehículo ni carrocería con el peso total de la ruta** (el declarado o la suma de sus mercancías); la Mesa de Costeo de Nexo lo resuelve. Se registra con `TryAddScoped`: #133 registra su implementación (un camión por fila de la recomendación) y si el cubicador falla la captura no guarda nada.
- **Conexos en la captura (decisión 3).** La ruta trae `servicios_conexos[]` (id del catálogo de Nexo y cantidad). Cotifly lee el `costoUnitario` del catálogo (`IClsCostosConexosNexo`, `GET /api/servicios-conexos` paginado: Nexo no tiene `GET` por id; el costo nunca sale al comercial) y los guarda en `CotizacionServiciosConexos` con ese costo como precio, con el método existente de la ruta (`AgregarOActualizarConexo`), así que ya suman a `costoServiciosConexos`. Errores por ruta, junto con los demás: `CONEXO_NO_ENCONTRADO`, `CONEXO_DUPLICADO`, `DEMASIADOS_CONEXOS` (20). El catálogo se lee con una caché en memoria de 5 minutos (`clsCostosConexosEnCache`): **el precio guardado es el costo de Nexo al momento de capturar** y ni el reintento ni el polling vuelven a cotizar los conexos. Enviarlos a Nexo pegados al primer camión es de #134; el margen y la exclusión, de #135.
- **Raíz.** Método nuevo `clsCotizacionSolicitud.RegistrarCaptura(encabezado, rutas)` (`clsRutaCapturada`: línea, línea de negocio, índice de origen, camiones `clsCamionPlaneado` y conexos); `RegistrarSolicitudCosteo` y `RegistrarSolicitudLicitacion` no cambian. `rutaOrigenIndice` es la posición de la ruta en el formulario y la fila del Excel en la licitación.
- **Chat.** El asistente por folio no crea cotizaciones y no depende de los campos retirados: lee el vehículo del camión (el que puso la Mesa) solo para mostrarlo; la herramienta `generar_pdf` está retirada del plugin (responde `HERRAMIENTA_DESCONOCIDA`) y la acción `confirmar_pdf` solo la ejecuta el frontend con el endpoint determinista `POST .../costeo/pdf` (ADR-018). `clsGenerarPdfService`, que crea una cotización "del chat" sin municipios (`RegistrarCotizacionChat`), queda **apagado por defecto** (`Pdf:PermitirCotizacionChat = false`, responde `COTIZACION_CHAT_RETIRADA`); solo las pruebas del PDF legado lo encienden.
- **Formato plano** (una ruta en la raíz): obsoleto, `deprecated` en `openapi.yaml`; se retira en #138. Con `rutas`, cualquier campo plano en la raíz es 400 con `detalle.errores` (nunca se descarta en silencio).
- **Nombre de la mercancía:** hoy se guarda el que manda el cliente; copiarlo del catálogo del cubicador depende de #133.

## Adaptaciones mínimas (las extensiones van en #132 a #137)

| Adaptación | Issue que la completa |
|---|---|
| Una ruta con **un** camión en el formulario y el chat; N rutas con un camión en las licitaciones. Vehículo y carrocería siguen saliendo del formulario | #132 (hecho: rutas en el formulario, camión sin vehículo), #133 (cubicador) |
| Las posiciones solo viajan a Nexo cuando la ruta tiene un único camión | #133 |
| El emparejamiento con la foto de Nexo deja sin asociar los camiones empatados (no desempata por orden) | #134 |
| El polling no importa los conexos de Nexo (la fuente es Cotifly, decisión 3), salvo la marca de "agregado sin relectura" de #124 | #134 |
| Excluir un conexo lo **borra** de la ruta (ya no hay columna `incluido`); para volver a incluirlo se agrega de nuevo | #135 |
| El ajuste de margen aplica el mismo margen a todas las rutas; `MargenAjustes.lineaSolicitudId` = ruta 1 | #135 |
| El precio de la ruta (y el de la vista de costeo) incluye los conexos; el PDF y el Excel imprimen la tarifa del transporte y los conexos por separado, y el total del pliego sigue sin conexos | #136 |
| La re-solicitud en el mismo folio descarta los conexos (la captura nueva de Nexo empieza sin ellos; se avisan en `conexos_descartados`) | #136 |
| El chat no guarda municipios (ni id ni nombre): su ruta queda sin ubicación (decisión 2) | #132: el chat ya no crea cotizaciones (solo queda `clsGenerarPdfService`, sin endpoint) |
| `IdTipoMercancia` no se exige en la captura vieja (`clsMercancia.DesdeCaptura`) | #132 (hecho: `ErroresCaptura` lo exige en toda captura nueva) |
| Los ids de las ciudades de devolución y levante no se resuelven ni se guardan (solo el nombre) | #132 (hecho) |
| Cotizaciones migradas por el 0009 cuyo `precioFinalLinea` no incluye los conexos: se recalculan con el próximo cambio de costo, conexos o margen (mientras tanto el PDF usa el transporte calculado) | #136 |
| Métricas por cotización: costo = Σ `costoFinal` de las rutas y margen ponderado por costo; la ciudad es la de la ruta 1 | #137 |
| La API acepta `valor_estimado` y `referencia` y los ignora; también `tipo_vehiculo` y `carroceria` (con aviso en el log) y el formato plano de una ruta | #138 |
| A Nexo viaja una sola `lineaNegocio` (la de la primera ruta) aunque cada ruta tenga la suya; Nexo admite `lineaNegocio` por línea | #134 |
| Los conexos capturados con la ruta quedan en Cotifly; enviarlos a Nexo pegados al primer camión | #134 |

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
