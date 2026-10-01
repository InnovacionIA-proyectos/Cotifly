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

## Alternativas consideradas

- **Plantilla fija de Cotifly:** obliga al comercial a copiar el pliego a mano en otra plantilla, que es lo que HU-011 busca evitar. Descartada.
- **Mapeo por nombre de columna en el servidor (diccionario de sinónimos):** frágil con plantillas nuevas y no resuelve unidades (toneladas, metros); el mapeo visual del frontend, como en Nexo, es más confiable. Descartada.

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
