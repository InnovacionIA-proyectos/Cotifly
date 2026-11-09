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

## HU-035.2 (#213): el pliego de licitación con los mismos controles

La auditoría de la #202 encontró que `POST /web/licitaciones/analizar-excel` abría el pliego con ClosedXML
(`clsLectorExcelLicitacionClosedXml`, para `filas_excel`) sin estos controles: una bomba zip o un libro denso de unos 186 KB (más de
1 GB y unos 15 s de CPU por lectura) podían tumbar el pod, y el archivo se enviaba a NEXO antes de leerlo en Cotifly.

1. **Pieza compartida `clsLecturaExcelAcotada` (Infraestructura/Comun, singleton).** Sale del lector de mercancías y la usan los dos
   lectores: `VerificarPaquete` (firma `PK`, 1000 entradas, el libro (`xl/workbook.xml` o, si otra herramienta lo nombró distinto, la
   parte a la que apunta la relación `officeDocument` de `_rels/.rels`, como lo abre ClosedXML), 64 MB descomprimidos contados de verdad),
   `VerificarElementos` (1.000.000 de elementos XML contados en flujo, sin DTD ni resolutor) y `LeerAsync<T>`, que dentro del cupo
   corre las dos verificaciones y después la lectura con ClosedXML del llamador. Mismos códigos y motivos (`EXCEL_INVALIDO` y
   `ARCHIVO_DEMASIADO_GRANDE` con `detalle.motivo`); sin códigos nuevos.
2. **Un solo cupo de 4 lecturas por proceso**, compartido por el Excel de mercancías y el pliego: el riesgo es la memoria del proceso,
   que es una sola. Lleno 15 s: 503 `SERVICIO_OCUPADO` con `Retry-After`. La cancelación libera el cupo.
3. **Orden del flujo de `POST /web/licitaciones/analizar-excel`:** validación del nombre y el tamaño → **lectura local** (cupo,
   controles y ubicación de las filas de las primeras 20 hojas con la regla de NEXO) → NEXO → cruce de `filas_excel` con las hojas de
   NEXO → sugerencias del catálogo. Antes la lectura local iba después de NEXO y sus fallos se ignoraban. Ahora un rechazo de los
   controles, el cupo lleno o un libro que ClosedXML no puede abrir (`LIBRO_ILEGIBLE`) responden **antes de NEXO**: NEXO nunca recibe
   basura ni bombas, y como NEXO usa la misma versión de ClosedXML, un libro que Cotifly no abre tampoco lo habría leído NEXO. El libro
   se lee una sola vez y el cupo se libera antes de llamar a NEXO (que puede tardar segundos), así una espera de NEXO no ocupa el
   cupo. Se descartó leer después de NEXO (mandaría el archivo sin revisar) y verificar antes pero leer después (descomprime dos veces y
   ocupa el cupo dos veces).
4. **`POST /web/licitaciones`** usa el mismo lector para la hoja del archivo: también pasa por los controles. Con archivo la hoja es
   obligatoria, así que todo original que se guarda (y que la exportación abre después con ClosedXML) pasó los controles.
5. **Tope de hojas** en el análisis: `filas_excel` se calcula para las primeras 20 hojas (las mismas del Excel de mercancías); las
   demás siguen en el análisis de NEXO, sin `filas_excel`.
6. **Exportación (`GET /web/licitaciones/{folio}/excel`).** Los originales guardados antes de este cambio nunca pasaron los
   controles, y `clsExportadorExcelLicitacionClosedXml` los abría con ClosedXML sin ellos. El exportador también usa
   `clsLecturaExcelAcotada.LeerAsync` (el puerto `IClsExportadorExcelLicitacion` pasa a `GenerarAsync`): controles y cupo compartido
   antes de abrir el original. Un original que no los pasa responde **400** `EXCEL_INVALIDO` o `ARCHIVO_DEMASIADO_GRANDE` con
   `detalle.motivo`, el mismo 400 que el contrato ya declaraba para un original ilegible (`EXCEL_INVALIDO`/`EXCEL_SIN_DATOS`): se
   descartó un 409 o 422 porque cambiaría el estado HTTP de los errores ya documentados de este endpoint, y mapear `EXCEL_INVALIDO`
   a 409 en general rompería los endpoints de subida. Un original que ClosedXML abre pero no puede recorrer o guardar es
   `EXCEL_INVALIDO` (`LIBRO_ILEGIBLE`), no un 500. Con el cupo lleno, 503 `SERVICIO_OCUPADO` (nuevo en este endpoint). Sin original
   no se abre nada del cliente y no se toma el cupo. Sin códigos nuevos.

## HU-035.3 (#277): qué partes del libro se cuentan como XML

El conteo de elementos (punto 5) leía como XML toda parte con «xml» en el tipo. Las partes binarias de Office también lo tienen
(`openxmlformats`): la configuración de impresión que guarda Excel (`xl/printerSettings/printerSettings1.bin`, tipo
`...spreadsheetml.printerSettings`), los objetos incrustados (`...oleObject`) o las propiedades personalizadas. Llegaban al lector
XML y el libro, válido, era `LIBRO_ILEGIBLE`; en pliegos de clientes la impresión configurada es lo normal.

1. Se lee como XML la parte cuyo tipo, sin parámetros, termina en `+xml` o es `application/xml` o `text/xml`, y además el dibujo VML
   (`...vmlDrawing`, sin `+xml`), que ClosedXML lee como XML para los comentarios: dejarlo fuera abriría un hueco en el tope de
   elementos. Las demás partes se saltan, como ya se saltaban las imágenes y el `vbaProject`.
2. Los topes (entradas, 64 MB descomprimidos, 1.000.000 de elementos) y el rechazo del DTD no cambian. Una parte que ClosedXML lee
   como XML no puede saltarse el conteo cambiando su tipo en `[Content_Types].xml`. El SDK de Open XML exige el tipo de las partes de
   **tipo fijo** (hoja, cadenas, estilos, VML...) y, si no coincide, el libro no abre (`LIBRO_ILEGIBLE`). La parte principal
   (`xl/workbook.xml`, `WorkbookPart`) **no** tiene tipo fijo: el SDK la abre con cualquier tipo y ClosedXML la lee igual, así que se
   cuenta **siempre**, declare el tipo que declare (auditoría de #277: un `workbook.xml` de 10 millones de elementos declarado
   `application/octet-stream`, `image/png` o `...sheet.main` sin `+xml` pasaba el control en un zip de 90 KB y ClosedXML ocupaba
   unos 1,35 GB). Las demás partes sin tipo fijo (imágenes, XML personalizado, propiedades, incrustadas, miniatura) ClosedXML no las
   lee como XML. Hay pruebas con una hoja y con el libro principal densos y con DTD declarados con tipos no XML.
3. Los tres lectores (mercancías, pliego y exportación) usan esta misma pieza: ninguno tiene un filtro propio.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Visto bueno de la ruta (`/web/mercancias/analizar-excel`) y de reutilizar códigos con `detalle.motivo` en vez de códigos nuevos | Usuario |
| Confirmar los topes (20 hojas, 50 columnas, 1000 filas, 64 MB descomprimidos, 1.000.000 de elementos XML, 4 simultáneos) con el uso real | Usuario / frontend |
| Integrar el endpoint en la pantalla "Líneas de solicitud" | Frontend (`clientepro-frontend`) |
| HU-035.2: visto bueno del cupo único (4 lecturas de Excel por proceso para los dos endpoints), del tope de 20 hojas para `filas_excel` y de rechazar antes de NEXO un libro que Cotifly no abre | Usuario |
| HU-035.2: los originales de licitación guardados antes de este cambio que no pasen los controles ya no se exportan (400 con `detalle.motivo`, punto 6); si aparece alguno real, decidir si se exporta sin original | Usuario |
