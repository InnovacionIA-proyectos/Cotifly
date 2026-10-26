# ADR-024 — Excel de mercancías de una ruta leído en Cotifly, sin NEXO (HU-035.1)

- **Estado:** Propuesta (2026-10-26). Issue #202 (HU-035.1), milestone HU-035. Pendiente del visto bueno del usuario (ver Pendientes).
- **Relacionadas:** ADR-017 (licitaciones desde Excel: análisis en NEXO), ADR-020 (catálogo del cubicador), HU-023.5 (#160: código de
  mercancía sugerido en el análisis del pliego).

## Contexto

En "Líneas de solicitud" la comercial quiere importar las mercancías de una ruta (tipo, cantidad, largo, ancho, alto, peso) desde un
Excel. El frontend asumía que `POST /web/licitaciones/analizar-excel` era un lector genérico de grilla, pero ese endpoint **reenvía el
archivo a NEXO** (`POST {Nexo:UrlBase}/api/captura-solicitudes/analizar-excel`), que espera el formato de un pliego de licitación: con un
Excel de mercancías sueltas falla o devuelve algo sin sentido, y además depende de que NEXO esté arriba y de que el comercial tenga el rol
de Cabina de Fletes.

## Decisión

1. **Endpoint nuevo `POST /web/mercancias/analizar-excel`.** Se descartó `/web/excel/analizar` (genérico): el contrato está organizado
   por recurso (`/web/licitaciones/analizar-excel`, `/web/tipos-mercancia`, `/web/cubicacion`) y este análisis es de mercancías (sugiere
   códigos del catálogo del cubicador); el sufijo `analizar-excel` es el mismo de la licitación, así el frontend reconoce la operación.
   Un lector genérico, si hiciera falta para otro recurso, se expondría con otra ruta reutilizando el mismo puerto.
2. **Lectura local con ClosedXML**, la librería que ya usa el backend para el Excel de licitaciones (MIT, misma versión que NEXO). Puerto
   `IClsLectorGrillaExcel` (Aplicación) e implementación `clsLectorGrillaExcelClosedXml` (Infraestructura). No usa
   `clsAnalizadorExcelNexo` ni nada de NEXO.
3. **Grilla cruda y mapeo en el frontend.** La respuesta `AnalisisExcelMercancias` tiene la misma forma de `AnalisisExcel` (para reutilizar
   la pantalla de mapeo) más los indicadores de truncado. El encabezado usa la regla del pliego (primera de las 15 primeras filas con 3
   valores distintos) con un respaldo (la primera fila con datos) para hojas de menos de 3 columnas. **Hojas ocultas** (decisión del
   usuario, 2026-10-26): las hojas `hidden` y `veryHidden` no se omiten; se leen como las visibles y vienen con `oculta = true` (las
   visibles, `false`). Un cliente puede dejar las mercancías en una hoja oculta, y omitirla haría perder datos sin aviso; el frontend
   decide si las ofrece (sugerido: preseleccionar una visible y mostrar las ocultas al final).
4. **Formato estable de las celdas**, sin cultura: números invariantes con 15 cifras significativas, fechas ISO, booleanos
   `true`/`false`. **Las fórmulas nunca se evalúan**: se devuelve el valor que Excel guardó (`CachedValue`); sin él, vacío. Evaluar
   fórmulas de un archivo no confiable es superficie de ataque y costo de CPU sin acotar.
5. **Robustez ante archivos maliciosos o corruptos** (siempre 400 con código, nunca 500): revisión del paquete antes de abrirlo (firma,
   1000 entradas, `xl/workbook.xml`, 64 MB descomprimidos contados leyendo de verdad: una bomba zip no llega a ClosedXML, que carga el
   libro entero en memoria; y 1.000.000 de elementos XML en todo el libro, contados en flujo sin DTD: un libro de 200 KB con 4 millones
   de celdas mínimas cabe en los 64 MB pero ocupaba más de 1 GB y 15 s de CPU en ClosedXML; con el tope, la lectura más pesada aceptada
   ronda 270 MB), topes de 20 hojas, 50 columnas, 1000 filas, 500 caracteres por celda y 100.000 celdas por respuesta, y un cupo
   de 4 lecturas simultáneas por proceso (503 `SERVICIO_OCUPADO` tras 15 s). Sin códigos de error nuevos: `EXCEL_INVALIDO` y
   `ARCHIVO_DEMASIADO_GRANDE` con `detalle.motivo` (`NO_ES_XLSX`, `CIFRADO_O_XLS`, `ZIP_INVALIDO`, `SIN_LIBRO`, `DEMASIADAS_ENTRADAS`,
   `LIBRO_ILEGIBLE`, `DESCOMPRIMIDO_EXCEDE_LIMITE`).
6. **Reutilización.** La validación genérica del archivo sale de `clsArchivoLicitacion` a `clsArchivoExcel` (Dominio/Comun; el pliego
   delega en ella). La detección de la columna de mercancía y las sugerencias del catálogo salen de `clsLicitacionService` a
   `clsDetectorMercanciaExcel` (Aplicación/Cubicacion), compartido por los dos análisis. La lectura del multipart sale del controller de
   licitaciones a `clsArchivoMultipart`.
7. **Catálogo opcional.** Sin cubicador, con él caído o con una excepción inesperada, el análisis sale igual sin sugerencias
   (`catalogo_mercancias_disponible = false`).
8. **Mismo rol y límites que la captura:** política `Comercial` (comercial o gerente), 12 MB de cuerpo
   (`SeguridadHttp:LimiteCuerpoLicitacionBytes`), 415 sin multipart, CORS y contrato de errores comunes.

## Alternativas consideradas

- **Reutilizar `licitaciones/analizar-excel`:** depende de NEXO y de su formato de pliego (el origen del problema).
- **`/web/excel/analizar` genérico:** más reutilizable, pero mezcla un lector sin recurso con las sugerencias de mercancía; se puede
  agregar después sobre el mismo puerto.
- **ExcelDataReader** (más liviano, streaming): sería una dependencia nueva; ClosedXML ya está aprobada y, con la revisión previa del
  paquete, su carga en memoria queda acotada.
- **Evaluar fórmulas sin valor guardado:** descartado por seguridad y costo (punto 4).

## Consecuencias

- El frontend usa este endpoint para "Importar mercancías desde Excel" y reutiliza su pantalla de mapeo; NEXO caído no lo afecta.
- Sin script de base de datos ni configuración nueva. El cupo de 4 lecturas es por réplica.
- Un Excel generado por una herramienta que no guarda los valores de las fórmulas trae esas celdas vacías.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Visto bueno de la ruta (`/web/mercancias/analizar-excel`) y de reutilizar códigos con `detalle.motivo` en vez de códigos nuevos | Usuario |
| Confirmar los topes (20 hojas, 50 columnas, 1000 filas, 64 MB descomprimidos, 1.000.000 de elementos XML, 4 simultáneos) con el uso real | Usuario / frontend |
| Integrar el endpoint en la pantalla "Líneas de solicitud" | Frontend (`clientepro-frontend`) |
