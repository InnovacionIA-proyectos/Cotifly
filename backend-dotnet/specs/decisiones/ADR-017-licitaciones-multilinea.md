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
- **Columnas de línea de `Cotizaciones` obsoletas, no borradas.** En una cotización de una línea (`POST /web/solicitudes-costeo`) el backend las sigue escribiendo como espejo de la línea 1: métricas (mapa por ciudad), "Mis cotizaciones" y el PDF de una línea no cambian. En una licitación quedan en `null`. Un script futuro las elimina cuando nada las lea.
- `/health/ready` responde 503 `esquema incompleto: falta 0005` sin el script. **Precondición de despliegue** (`DEPLOY.md`).

### 3. Costeo por línea, sin cambiar el de una línea

- **Envío:** `CapturaSolicitudRequest` con todas las líneas en orden; la línea de negocio de Nexo de la licitación viaja en cada una. Mismo cliente `NexoCosteo`, misma regla de "confirmar antes de llamar a Nexo", mismos estados de error y el mismo reintento (`POST /web/cotizaciones/{folio}/costeo/reintentar`, que reenvía todas las líneas guardadas).
- **Ids de las líneas en Nexo:** `CapturaResultDto` no los devuelve; el polling los toma de `GET /api/solicitudes/{id}` (`solicitud.lineas[].id`) **por orden** la primera vez y los guarda. Si Nexo devuelve otro número de líneas no se adivina: la cotización queda con el motivo y sin precio. **Pendiente de confirmar con Nexo** que `lineas[]` viene en el orden de captura (o que agregue una referencia externa por línea).
- **Estado por línea:** la misma regla de ADR-015 §4 (`clsDerivacionEstadoCosteo.DerivarLinea`): `Descartada` y `Entregada` aplican a todas; si no, la línea con flete queda `autocosteo` y la que no, `pendiente_costeo`. El estado de la cotización no cambia de regla (todas con flete → `autocosteo`). Con la edad máxima, las líneas sin flete pasan a `costeo_vencido` con la cotización.
- **Margen y precio por línea:** el margen se resuelve una vez al enviar (una línea de negocio por licitación) y se aplica a cada línea en cuanto tiene flete, aunque otras sigan pendientes. La cotización suma los precios de sus líneas solo cuando todas tienen flete; si Nexo la entrega con líneas sin flete, la cotización no tiene precio total y queda el motivo. Con PDF ya generado los precios se conservan (solo cambia el estado). **Con una sola línea se usa exactamente la regla de HU-005.7** (mismo precio, mismos mensajes y el mismo "cambió"), lo verifica una prueba de equivalencia.
- **Servicios conexos:** siguen por cotización, como en Nexo (pregunta abierta con negocio).
- **Moneda:** solo COP, como el costeo de ADR-015 §5; el modelo ya guarda `moneda` y `trmAplicada` por cotización para cuando el costeo soporte USD.

### 4. Endpoints (HU-011.2)

- `POST /web/licitaciones/analizar-excel`: proxy de `analizar-excel` de Nexo (multipart `archivo`, 10 MB, `.xlsx`/`.xlsm`); no guarda nada. Los errores del archivo se validan antes de llamar a Nexo con los mismos códigos de Nexo.
- `POST /web/licitaciones`: multipart (`licitacion` JSON + `archivo` opcional) o JSON. Valida todo antes de guardar y responde `400 ReporteLicitacionInvalida` con todas las filas que fallaron. Con archivo, `hoja` y la `fila` de cada línea son obligatorias y se verifican contra el archivo con un **puerto 1:1 de la detección de Nexo** (ClosedXML), para que la `fila` del análisis apunte a la misma fila del archivo. Tope de 1000 líneas.
- `GET /web/licitaciones/{folio}`: estado y precio por línea (nunca costos).
- Los cuerpos se leen a mano (no `[FromForm]`) para que la sesión se valide antes que el tipo de contenido; límite de cuerpo propio de 12 MB (`SeguridadHttp:LimiteCuerpoLicitacionBytes`). Mismos roles y aislamiento por dueño que el resto de `/web/*`.

## Alternativas consideradas

- **Plantilla fija de Cotifly:** obliga al comercial a copiar el pliego a mano en otra plantilla, que es lo que HU-011 busca evitar. Descartada.
- **Mapeo por nombre de columna en el servidor (diccionario de sinónimos):** frágil con plantillas nuevas y no resuelve unidades (toneladas, metros); el mapeo visual del frontend, como en Nexo, es más confiable. Descartada.
- **Mover los campos de línea y borrar las columnas de `Cotizaciones` en el 0005:** rompía métricas, "Mis cotizaciones" y el PDF de una línea en el mismo despliegue y no es reversible. Se dejan como espejo obsoleto.
- **Analizar el Excel en Cotifly en vez de en Nexo:** duplicaría el analizador; se reutiliza el de Nexo y Cotifly solo porta la detección de filas para ubicar la fila al exportar.
- **Guardar el Excel original en disco o en el almacén de PDF:** otro volumen que respaldar y otro control de acceso; en la base queda con la cotización, con el mismo aislamiento por dueño y los mismos respaldos.
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
| Confirmar que `GET /api/solicitudes/{id}` devuelve `lineas[]` en el orden de captura, o una referencia externa por línea en el alta | Equipo de Nexo |
| Confirmar que `analizar-excel` acepta el rol del comercial cuando lo llama Cotifly (hoy `Comercial`, `Admin`) | Equipo de Nexo |
| Licitaciones en USD (el costeo de ADR-015 sigue solo en COP) | Duban (integración con HU-012) |
