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
  2. un NULL mezclado con estados cuenta como `pendiente_envio`;
  3. `descartada` se ignora salvo que todos lo sean;
  4. si todos los que quedan coinciden, ese estado;
  5. si no, el primero de `error_envio` → `costeo_vencido` → `pendiente_envio` → `pendiente_costeo` → `entregada` → `autocosteo`.

  Las transiciones de la solicitud (envío, error de envío, vencimiento, descarte) se aplican a los camiones y la cabecera vuelve a ser el resumen de sus rutas.
- **Invariantes que se conservan:** `versionNegocio` y el candado de `clsUnitOfWork`, `sp_getapplock` (#125), `pdfDesactualizado` y la regla del PDF, `textosCotizacion`, resultado y "perdida" solo en Cotifly (#127), `costeoConsultasFallidas` y los reclamos del polling. `costeoUltimaConsultaAt` ya no se usa (el `0009` lo borró).
- **Margen sugerido dividido:** la ruta guarda `margenPoliticaSugerido` y `margenSAPSugerido`; el vigente es el de SAP si existe y si no el de la política. El ajuste de margen guarda la ruta en `MargenAjustes.lineaSolicitudId`.

### Script `0010_nombres_municipio.sql` (borrador, pendiente de revisión de scalderon-bs)

Idempotente, no destructivo y en una transacción. Agrega a la ruta `origenNombre`, `destinoNombre`, `ciudadDevolucionNombre` y `ciudadLevanteNombre` (los ids de devolución y levante ya los trae el `0009`; se agregan solo si faltan), y además:

- `CotizacionesSolicitud.solicitudNexoId`: el `0009` borró `costeoId` como "nunca usado", pero es el id de la **solicitud** de Nexo que devuelve el alta y consulta el polling (`cotizacionNexoId` es el de la cotización de Nexo). Sin él no hay polling.
- `CotizacionServiciosConexos.nombreServicioConexo`: copia del nombre del conexo para el PDF (mismo criterio de la decisión 2).
- `ck_cotizaciones_estadoEnvio` si falta (la base compartida se instaló antes de que el `0009` lo trajera).
- Si existen los respaldos `Respaldo0009_*`, llena desde ellos los nombres y `solicitudNexoId` vacíos.

`db/scripts/01_crear_esquema.sql` ya trae esas columnas; una prueba de integración verifica que la instalación desde cero y las migraciones `0001` → `0010` dejan el mismo esquema.

### Sonda `/health/ready`

`clsBaseDatosHealthCheck` se rehízo desde el modelo EF: compara las tablas y columnas mapeadas con `INFORMATION_SCHEMA` y responde `falta 0002` (sin las tablas de usuarios), `falta 0009` (esquema anterior: no hay `CotizacionesSolicitud`), `falta 0010` (faltan las columnas del `0010`) o `falta <Tabla.columna>` para cualquier otra diferencia.

### Qué reemplaza

- **ADR-015** (costeo en Nexo): el id de la solicitud pasa de `Cotizaciones.costeoId` a `CotizacionesSolicitud.solicitudNexoId`; el costo, el margen y el precio son de la ruta; el estado se guarda en el camión y se resume en ruta y cabecera; `valorEstimado` y `referencia` ya no viajan a Nexo.
- **ADR-017** (licitaciones): cada fila del Excel es una ruta con un camión; las columnas de línea de la cabecera (`origen`, `destino`, `costo`, `lineaNegocioId`…) ya no existen; `filaOrigen` es `rutaOrigenIndice`.
- **ADR-018**: §3 (margen) pasa a ser por ruta; §12 (conexos) deja el JSON y usa filas de la ruta; la tabla de idempotencia irá en el siguiente script libre (hoy el `0011`).

## Adaptaciones mínimas (las extensiones van en #132 a #137)

| Adaptación | Issue que la completa |
|---|---|
| Una ruta con **un** camión en el formulario y el chat; N rutas con un camión en las licitaciones. Vehículo y carrocería siguen saliendo del formulario | #132, #133 (cubicador) |
| Las posiciones solo viajan a Nexo cuando la ruta tiene un único camión | #133 |
| El emparejamiento con la foto de Nexo deja sin asociar los camiones empatados (no desempata por orden) | #134 |
| El polling no importa los conexos de Nexo (la fuente es Cotifly, decisión 3), salvo la marca de "agregado sin relectura" de #124 | #134 |
| Excluir un conexo lo **borra** de la ruta (ya no hay columna `incluido`); para volver a incluirlo se agrega de nuevo | #134 |
| El ajuste de margen aplica el mismo margen a todas las rutas; `MargenAjustes.lineaSolicitudId` = ruta 1 | #134, #135 |
| El precio de la ruta (y el de la vista de costeo) incluye los conexos; el PDF (de una ruta o de pliego) imprime la tarifa del transporte y los conexos por separado, y el total del pliego sigue sin conexos | #136 |
| `IdTipoMercancia` no se exige en la captura vieja (`clsMercancia.DesdeCaptura`) | #132 |
| No se resuelven los ids de las ciudades de devolución y levante | #132 |
| Métricas por cotización: costo = Σ `costoFinal` de las rutas y margen ponderado por costo; la ciudad es la de la ruta 1 | #137 |
| La API acepta `valor_estimado` y `referencia` y los ignora | #138 |

## Alternativas consideradas

- **Mantener las columnas de línea en la cabecera como espejo de la ruta 1:** descartada; el `0009` las borró y duplicarían la fuente de verdad.
- **Guardar el estado solo en la cabecera:** descartada; con varios camiones Nexo costea cada uno por separado.
- **Seguir con `costeoId` en `cotizacionNexoId`:** descartada; son dos ids distintos de Nexo (solicitud y cotización).

## Consecuencias

- El backend vuelve a funcionar sobre `0001` → `0009` + `0010` (o `01_crear_esquema.sql`). **Sin el `0010`, `/health/ready` responde `503 falta 0010`**: hay que aplicarlo antes de desplegar.
- Los costeos guardados antes del `0009` sin respaldos no tienen `solicitudNexoId`: el polling no los puede consultar (quedan como están).
- El precio de la cotización incluye los conexos (antes se sumaban aparte en la vista).

## Pendientes

| Pendiente | Dueño |
|---|---|
| Revisión y aplicación del `0010` en la base compartida (después del `0009`, antes de desplegar) | scalderon-bs y Duban |
| Extensiones de la tabla de adaptaciones | #132 a #137 |
| Contrato de la API y `openapi.yaml` | #138 |
