# ADR-017 — Licitaciones desde Excel y cotizaciones de varias líneas

- **Estado:** Propuesta (formato y preguntas de negocio pendientes de aprobación de Gerencia Comercial; script 0005 pendiente de aprobación de scalderon-bs y del DBA; varios puntos con Nexo, ver Pendientes)
- **Fecha:** 2026-10-01
- **Issue / HU:** #46 (HU-011.1), #47 (HU-011.2), #48 (HU-011.3) y #49 (HU-011.5). Amplía ADR-015 (costeo en Nexo, que dejó la deuda "HU-011 multi-línea"). Relacionada con ADR-006 (PDF), ADR-008 (esquema solo por scripts), ADR-011 (folio) y ADR-014 (TRM y USD).
- **Nota de numeración:** ADR-016 es la de métricas (HU-010.1); esta queda como ADR-017.

## Contexto

- HU-011 pide cotizar licitaciones (pliegos) que el cliente manda en Excel: cargarlas, enviarlas a Nexo como una sola solicitud con varias líneas, exportar el Excel con los precios y generar un PDF de pliego.
- Cada cliente manda su propia plantilla. Nexo ya tiene un flujo para esto: `POST /api/captura-solicitudes/analizar-excel` (no persiste; devuelve hojas, encabezados y filas para que el usuario **mapee** columnas) y `POST /api/captura-solicitudes` con `lineas[]` y `tipo = licitacion`.
- ADR-015 fijó "una cotización de Cotifly = una solicitud de Nexo con una línea", con los campos de la línea en `Cotizaciones` (script 0004) y la deuda consciente de moverlos a `CotizacionLineasSolicitud` para HU-011.
- Nexo guarda los servicios conexos a nivel de cotización (`CotizacionConexoDto` no tiene `lineaSolicitudId`) y su `CapturaResultDto` no devuelve los ids de las líneas creadas; los ids salen de `GET /api/solicitudes/{id}` (`solicitud.lineas[].id`).

## Decisión

### 1. Formato del Excel (HU-011.1, #46)

- **Cotifly no impone plantilla.** Reutiliza el flujo de Nexo de analizar y mapear: el comercial sube el pliego, Cotifly reenvía el archivo al análisis de Nexo, el frontend mapea las columnas a los campos destino y envía las líneas ya mapeadas. Cualquier plantilla de cliente sirve sin cambios de código.
- Los campos destino son los de `CapturaLineaRequest` de Nexo, con los nombres y límites de `POST /web/solicitudes-costeo`; el encabezado (cliente, línea de negocio, `deadline`, `valor_estimado`, `referencia`, horas) lo llena el comercial. El detalle completo (tipos, obligatoriedad, catálogos, validación por fila, columnas de la exportación y un ejemplo) está en `docs/HU-011-formato-excel-licitacion.md`.
- La validación por fila replica `CapturarSolicitudValidador` de Nexo y se ejecuta entera antes de guardar: el reporte trae todas las filas con error (índice, fila, campo y código), no solo la primera.

### 2. Modelo multi-línea (HU-011.2, #47): script 0005

- **Una cotización de Cotifly = una solicitud de Nexo con N líneas** (reemplaza la regla "una línea" de ADR-015 §1). Script `db/migrations_mssql/0005_lineas_solicitud.sql` (idempotente, no destructivo, **pendiente de aprobación de scalderon-bs y del DBA**):
  - `CotizacionLineasSolicitud` (1 a N): los campos de `CapturaLineaRequest`, `orden` (único por cotización, el orden en que viaja a Nexo), `filaOrigen` (índice de la fila del análisis), `lineaSolicitudNexoId`, `estadoCosteo` (los 7 códigos de ADR-015 §2), `costo` (interno), `precioFinal` y `serviciosConexos` (JSON, reservado);
  - `CotizacionPosiciones.lineaSolicitudId` (FK nullable): cada bulto pertenece a una línea; `orden` sigue único por cotización (las posiciones de la línea N siguen a las de la N - 1);
  - `CotizacionArchivosLicitacion` (1 a 1): el Excel original (máx. 10 MB, SHA-256, hoja mapeada) para la exportación de #48;
  - backfill: una línea (orden 1) por cada cotización con formulario ampliado, con costo y precio solo si el costeo estaba completo, y sus posiciones enlazadas.
- **Columnas de línea de `Cotizaciones` obsoletas, no borradas.** En una cotización de una línea (`POST /web/solicitudes-costeo`) el backend las sigue escribiendo como espejo de la línea 1: métricas (mapa por ciudad), "Mis cotizaciones" y el PDF de una línea no cambian. En una licitación quedan en `null`. Un script futuro las elimina cuando nada las lea (ver §7). **Consecuencia:** las licitaciones no aparecen en la concentración por ciudad del tablero de métricas (ADR-016), que lee `Cotizaciones.origen`/`destino`.
- Índice `ix_cotizacionPosiciones_lineaSolicitudId` (filtrado) sobre la FK nueva de las posiciones.
- `/health/ready` responde 503 `esquema incompleto: falta 0005` sin el script. **Precondición de despliegue** (`DEPLOY.md`).
- **Retención del Excel original (propuesta, pendiente de negocio y del DBA).** El pliego de un cliente es un documento confidencial de su proceso de compra y puede traer datos personales de contactos (Ley 1581 de 2012). Hoy se guarda sin fecha de caducidad en `CotizacionArchivosLicitacion`, viaja en los respaldos de la base y en cualquier copia de producción a pruebas. Se propone purgar el contenido (dejar nombre, hoja, tamaño y SHA-256) **N meses después del cierre** de la licitación (resultado marcado, `descartada` o `costeo_vencido`), con N a definir con Gerencia Comercial (propuesta: 12), con un trabajo programado o un script del DBA; y anonimizar o vaciar la tabla en las copias a ambientes de prueba. El SHA-256 es solo trazabilidad (probar qué archivo se recibió), no protege el contenido. Detalle operativo en `docs/10-operacion.md`.

### 3. Costeo por línea, sin cambiar el de una línea

- **Envío:** `CapturaSolicitudRequest` con todas las líneas en orden; la línea de negocio de Nexo de la licitación viaja en cada una. Mismo cliente `NexoCosteo`, misma regla de "confirmar antes de llamar a Nexo", mismos estados de error y el mismo reintento (`POST /web/cotizaciones/{folio}/costeo/reintentar`, que reenvía todas las líneas guardadas).
- **Ids de las líneas en Nexo: asociación por CONTENIDO (riesgo documentado).** `CapturaResultDto` no devuelve los ids, y el orden de `solicitud.lineas[]` en `GET /api/solicitudes/{id}` **no es el de captura**: Nexo las carga con `Include(Lineas)` sin `OrderBy` y sus ids son GUID aleatorios. Asociar por posición cruzaba precios entre rutas (en la vista, el Excel y el PDF). Mitigación (`clsEmparejadorLineasNexo`): cada línea se asocia con la de Nexo por contenido, con los `lineas[]` de primer nivel de `SolicitudCosteoDto` (`ruta` "Origen → Destino", tipología, peso, tipo de vehículo y carrocería), normalizados (mayúsculas, sin tildes ni espacios repetidos) y sin contradicción en los datos que traen ambos lados. Solo se asocia un par único en ambos sentidos. Dos líneas idénticas, o una sin candidato (Nexo normalizó la ruta de otra forma, o hay otro número de líneas), **quedan sin asociar: sin precio y con el motivo**, y la licitación no tiene total. Los ids se guardan la primera vez y no se recalculan. Con una sola línea en cada lado la asociación es directa (HU-005.5). **Pedido al equipo de Nexo:** devolver `lineaIds[]` en el orden de entrada en `CapturaResultDto`, o aceptar una referencia externa por línea en `CapturaLineaRequest` y devolverla en `LineaSolicitudDto`; con eso la asociación deja de ser heurística.
- **Estado por línea:** la misma regla de ADR-015 §4 (`clsDerivacionEstadoCosteo.DerivarLinea`): `Descartada` y `Entregada` aplican a todas; si no, la línea con flete queda `autocosteo` y la que no, `pendiente_costeo`. El estado de la cotización no cambia de regla (todas con flete → `autocosteo`). Con la edad máxima, las líneas sin flete pasan a `costeo_vencido` con la cotización.
- **Margen y precio por línea:** el margen se resuelve una vez al enviar (una línea de negocio por licitación) y se aplica a cada línea en cuanto tiene flete, aunque otras sigan pendientes. Un costo negativo de Nexo no se usa ni rompe el polling: la línea queda sin precio y se advierte (como HU-005.7). La cotización suma los precios **ya redondeados** de sus líneas (no recalcula el margen sobre la suma de costos) y **sin servicios conexos**, solo cuando todas las líneas están asociadas y tienen flete; si Nexo la entrega con líneas sin flete, la cotización no tiene precio total y queda el motivo. Con PDF ya generado los precios se conservan (solo cambia el estado). **Con una sola línea se usa exactamente la regla de HU-005.7** (mismo precio, mismos mensajes y el mismo "cambió"), lo verifica una prueba de equivalencia.
- **Servicios conexos:** siguen por cotización, como en Nexo (pregunta abierta con negocio). No suman al total de la licitación: el Excel y el PDF rotulan el total "sin servicios conexos" y listan los conexos aparte.
- **Línea de negocio que viaja a Nexo:** el `mapeoLineasNexo` de la línea de Cotifly que elige el comercial, en cada línea. Nexo tiene además `LineaNegocio=Licitaciones`. **Pregunta abierta a Nexo y a negocio:** si una licitación debe viajar con `Licitaciones` (y cómo afecta la tarifa y la bandeja de la Mesa).
- **Líneas que Nexo nunca costea:** en una prueba real de Nexo (HU-13.4, pliego de 15 líneas) 3 quedaron sin flete. Hoy la licitación queda sin total y sin PDF. **Pregunta abierta a negocio:** permitir excluir una línea de la cotización, o un PDF parcial que la marque "no cotizada".
- **Moneda:** solo COP, como el costeo de ADR-015 §5; el modelo ya guarda `moneda` y `trmAplicada` por cotización para cuando el costeo soporte USD.

### 4. Endpoints (HU-011.2)

- `POST /web/licitaciones/analizar-excel`: proxy de `analizar-excel` de Nexo (multipart `archivo`, `.xlsx`/`.xlsm`); no guarda nada. Los errores del archivo se validan antes de llamar a Nexo con los mismos códigos de Nexo. **Tamaño:** Nexo limita a 10 MB el cuerpo completo; Cotifly acepta archivos de hasta 10 MB menos 64 KB para que el sobre multipart quepa, y un 413 de Nexo igual se traduce a `ARCHIVO_DEMASIADO_GRANDE`. **403 de Nexo** (el comercial no tiene el rol de Cabina de Fletes) es `NEXO_SIN_PERMISO` (403 en `/web/*`), no `COSTEO_NEXO_NO_DISPONIBLE`: es configuración del usuario, no una caída. En el alta, el mismo 403 deja la cotización en `error_envio` con ese motivo (reintentable cuando le den el rol).
- `POST /web/licitaciones`: multipart (`licitacion` JSON + `archivo` opcional) o JSON. Valida todo antes de guardar y responde `400 ReporteLicitacionInvalida` con todas las filas que fallaron. Con archivo, `hoja` y la `fila` de cada línea son obligatorias y se verifican contra el archivo con un **puerto 1:1 de la detección de Nexo** (ClosedXML), para que la `fila` del análisis apunte a la misma fila del archivo. Tope de 1000 líneas.
- `GET /web/licitaciones/{folio}`: estado y precio por línea (nunca costos).
- Los cuerpos se leen a mano (no `[FromForm]`) para que la sesión se valide antes que el tipo de contenido; límite de cuerpo propio de 12 MB (`SeguridadHttp:LimiteCuerpoLicitacionBytes`, 413 al pasarlo). Mismos roles y aislamiento por dueño que el resto de `/web/*`.
- **Lectura acotada del Excel** (`clsLectorExcelLicitacionClosedXml`): una celda lejana (`XFD3000`) o una combinada enorme (`A2:XFD1048576`) hacían recorrer o expandir millones de celdas (segundos y GB de memoria, o el proceso caído). Ahora solo se leen las celdas con contenido de las primeras 200 columnas, las combinadas se consultan como rangos recortados (sin expandirlas; más de 10.000 es `EXCEL_INVALIDO`), se ubican como máximo 1000 filas con datos y se respeta la cancelación. En un pliego normal el resultado es idéntico al de Nexo (prueba de paridad con el pliego de 300 líneas de Nexo y un oráculo copiado de su `ExcelSolicitudesAdapter`).
- **ClosedXML 0.104.2**, la misma versión de Nexo (sin vulnerabilidades conocidas en OSV al 2026-10-01): la detección de encabezados depende de `RangeUsed` y del formato de las celdas, así que usar la misma versión evita diferencias sutiles. Si Nexo sube de versión, se sube aquí y se corre la prueba de paridad.
- **Doble envío:** dos `POST /web/licitaciones` iguales crean dos licitaciones (y dos solicitudes en Nexo). No se implementa `Idempotency-Key` en esta iteración: exigiría una tabla nueva de claves por usuario (otro cambio de esquema para el DBA) y coordinarla entre réplicas, para un caso que el frontend evita bloqueando el botón mientras la petición está en curso (hasta unos 30 s). Queda como **responsabilidad documentada del frontend** (`docs/GUIA_FRONTEND_COTIFLY.md` §5.24); si aparecen duplicados en producción, se agrega el encabezado con su tabla.

### 5. Exportación del Excel con resultados (HU-011.3, #48)

- `GET /web/licitaciones/{folio}/excel` **reconstruye desde el archivo original** guardado en `CotizacionArchivosLicitacion` (§2). No se usa `GET /api/cotizaciones/{id}/exportar-excel` de Nexo: genera una plantilla propia de Nexo (no el pliego del cliente) y escribe el flete y los conexos con su valor de Nexo, es decir el costo bruto, que nunca debe llegar al cliente.
- Con el original: se ubica la hoja mapeada y sus filas con la misma regla de Nexo (§4) y se agregan tres columnas a la derecha de la última usada, en la fila del encabezado: `Precio final Cotifly ({moneda})` (precio con margen; COP sin decimales, USD con dos, ADR-014), `Estado costeo Cotifly` y `Servicios conexos Cotifly`. Ninguna celda del cliente se modifica. Las líneas sin costeo llevan `SIN COSTEO` con fondo amarillo; las filas del pliego que el comercial no envió, `Sin enviar`. Con subencabezado, las columnas nuevas combinan las dos filas del encabezado.
- Hoja nueva `Resumen Cotifly`: folio, cliente, NIT, referencia, moneda (y TRM si es USD), líneas costeadas y pendientes, total (solo si todas tienen precio; si no, "Pendiente: N línea(s) sin costeo") y los servicios conexos incluidos con su precio con margen. Como Nexo guarda los conexos por cotización, la columna de conexos de cada línea remite al resumen ("Aplican a toda la licitación"); si negocio decide conexos por línea, se llenan desde `CotizacionLineasSolicitud.serviciosConexos` sin cambiar el contrato.
- Sin original (licitación creada con JSON): una hoja `Licitación` con los campos mapeados y las mismas columnas.
- **El costo de Nexo no entra al exportador:** el objeto que recibe (`clsExportacionLicitacion`) solo trae precios y estados; una prueba de punta a punta verifica que ningún número del archivo sea un flete o un conexo de Nexo.
- Siempre un `.xlsx` **de verdad** (`{folio}.xlsx`): si el original era `.xlsm`, el paquete se convierte (tipo de documento `Workbook` y sin `vbaProject.bin`; si no, Excel se niega a abrirlo). Las macros del cliente no se exportan, y ClosedXML puede perder gráficos, imágenes y tablas dinámicas del original: la exportación es una copia de trabajo con los precios, no un reemplazo del archivo del cliente. Se conservan fórmulas, formatos, combinadas, validaciones, formatos condicionales, comentarios, nombres, tablas y hojas ocultas.
- Las columnas nuevas van a la derecha de la última columna usada de verdad (aunque el lector solo lea 200); "Sin enviar" solo marca las filas entre la primera y la última línea enviada (una fila de totales o notas del cliente al final no es una ruta). Una línea cuya fila ya no está en el pliego aparece en el resumen ("Líneas sin fila en el pliego original"), no se pierde en silencio. El total se rotula "Total de la licitación (sin servicios conexos)".
- Solo para licitaciones (`tipoSolicitud = licitacion` con líneas): una cotización spot o del chat responde 404, como cualquier recurso que no es del comercial. Solo el dueño. Se puede exportar en cualquier momento.

### 7. Seguimiento: retirar las columnas de línea obsoletas de `Cotizaciones`

`origen`, `destino`, `tipoVehiculo`, `carroceria`, `tipologia`, `claseViaje`, `claseContenedor`, `exclusivo`, `consolidado`, `retornaContenedor`, `ciudadDevolucionContenedor`, `levanteContenedorDiferente`, `ciudadLevanteContenedor`, `valorAprovisionamiento`, `pesoKg` y `valorMercancia` se retiran con un script futuro cuando estos consumidores lean de `CotizacionLineasSolicitud`:

| Consumidor | Qué lee |
|---|---|
| `clsMetricasRepository` (concentración por ciudad, HU-010.1) | `origen`, `destino` |
| `clsCosteoCotizacionService.Payload` (PDF de una línea) | `origen`, `destino`, `tipoVehiculo`, `carroceria`, `pesoKg` |
| `clsCotizacion.SolicitudCosteo` (reintento de cotizaciones anteriores al backfill) | todas |
| `clsCotizacion.RegistrarSolicitudCosteo` y el mapeo EF de `Cotizaciones` | escriben el espejo de la línea 1 |

**Regla mientras tanto:** ningún código nuevo lee esas columnas; las licitaciones solo usan `CotizacionLineasSolicitud`.

## Alternativas consideradas

- **Plantilla fija de Cotifly:** obliga al comercial a copiar el pliego a mano en otra plantilla, que es lo que HU-011 busca evitar. Descartada.
- **Mapeo por nombre de columna en el servidor (diccionario de sinónimos):** frágil con plantillas nuevas y no resuelve unidades (toneladas, metros); el mapeo visual del frontend, como en Nexo, es más confiable. Descartada.
- **Mover los campos de línea y borrar las columnas de `Cotizaciones` en el 0005:** rompía métricas, "Mis cotizaciones" y el PDF de una línea en el mismo despliegue y no es reversible. Se dejan como espejo obsoleto.
- **Analizar el Excel en Cotifly en vez de en Nexo:** duplicaría el analizador; se reutiliza el de Nexo y Cotifly solo porta la detección de filas para ubicar la fila al exportar.
- **Guardar el Excel original en disco o en el almacén de PDF:** otro volumen que respaldar y otro control de acceso; en la base queda con la cotización, con el mismo aislamiento por dueño y los mismos respaldos.
- **Usar `exportar-excel` de Nexo y agregarle columnas:** no es el archivo del cliente y trae los costos de Nexo; descartada.
- **Endpoint `lineas:bulk` de Nexo (cabecera + carga masiva):** es de `CoordinadorFletes` y `Admin`, no del comercial, y no resuelve rutas por texto libre; `captura-solicitudes` con `lineas[]` sí.

## Consecuencias

- El contrato con el backend es el conjunto de campos destino, no una plantilla: cambiar de cliente no cambia el backend.
- El frontend asume la traducción de textos del pliego a códigos de catálogo y de unidades.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Ejemplo real de un pliego de cliente para validar el formato | Negocio (Gerencia Comercial) |
| Campos obligatorios de una línea de licitación (propuesta: exigir también `tipo_vehiculo`) | Gerencia Comercial |
| Servicios conexos por línea o por licitación (propuesta: por licitación, como Nexo) | Gerencia Comercial |
| Una línea de negocio por licitación o por fila (propuesta: una por licitación) | Gerencia Comercial |
| Aprobar el DDL del `0005_lineas_solicitud.sql` (tablas, FK, backfill) y el crecimiento por el Excel original en `VARBINARY(MAX)` | scalderon-bs + DBA |
| Devolver `lineaIds[]` en el orden de entrada en `CapturaResultDto`, o aceptar y devolver una referencia externa por línea (hoy Cotifly asocia por contenido, §3) | Equipo de Nexo |
| Qué línea de negocio viaja en una licitación (`Licitaciones` de Nexo o la de ruta) | Equipo de Nexo + negocio |
| Qué hacer con las líneas que Nexo nunca costea (excluirlas o PDF parcial) | Gerencia Comercial |
| Retención y purga del Excel original (propuesta: purgar el contenido 12 meses después del cierre; anonimizar copias a pruebas) | Gerencia Comercial + DBA |
| Confirmar que `analizar-excel` acepta el rol del comercial cuando lo llama Cotifly (hoy `Comercial`, `Admin`) | Equipo de Nexo |
| Licitaciones en USD (el costeo de ADR-015 sigue solo en COP) | Duban (integración con HU-012) |
