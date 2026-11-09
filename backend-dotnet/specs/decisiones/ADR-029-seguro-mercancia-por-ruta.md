# ADR-029 — Seguro de la mercancía por ruta (HU-045.2)

- **Estado:** Propuesta (2026-11-06; §9 agregado el 2026-11-08). Issues #264 (HU-045.2) y #265 (HU-045.3), milestone HU-045. Pendiente del
  visto bueno del usuario (ver Pendientes).
- **Relacionadas:** HU-045.1 (#263, script `db/scripts/20_seguro_mercancia.sql`), HU-043.2 (#251, conexos a su venta sin margen),
  ADR-018 §3, §8 y §11 (margen, `If-Match`, regla del PDF), ADR-021 (edición de la solicitud), HU-036 (cotización finalizada),
  HU-045.3 (#265, la línea en el PDF, el Excel y el correo).

## Contexto

La HU-045 pide marcar en cada ruta si incluye el seguro de la mercancía (póliza específica de NOGE0076, la que se vende al cliente sin
póliza propia) y que Cotifly lo calcule como `valor_mercancia × tasa vigente` y lo sume al total. Las decisiones de negocio están en la
milestone (2026-11-02): el seguro no lleva margen (1), se calcula en pesos y en USD se convierte con la TRM de la solicitud (2), se marca
en el costeo con un endpoint propio con `If-Match` (3), sin valor de la mercancía responde `SEGURO_SIN_VALOR_MERCANCIA` (4), la tasa se
toma al marcar y otra vez si cambia el valor, nunca con la cotización finalizada o enviada (5), y no viaja a Nexo (6). Datos ya creó la
tabla `TarifasSeguro` (una sola tasa activa) y las columnas `incluyeSeguro`, `tasaSeguroAplicada` y `costoSeguro` de la ruta (script 20).

## Decisión

1. **Dominio.** La ruta (`clsLineaSolicitud`, parcial `Seguro`) guarda las tres columnas y las mantiene coherentes con los CHECK del 20:
   - `IncluirSeguro(tasa)`: sin `ValorMercancia`, `SEGURO_SIN_VALOR_MERCANCIA`; si ya lo tiene, nada cambia (conserva su tasa).
   - `QuitarSeguro()`: tasa y costo en `null`.
   - `costoSeguro = valorMercancia × tasa` en pesos, **sin redondear a pesos** (decisión del usuario del 2026-11-09). Se guarda con la
     escala de la columna `costoSeguro DECIMAL(18,2)` del script 20 (centavos; `valorMercancia` también es `DECIMAL(18,2)` y la tasa
     `DECIMAL(9,6)`): `SeguroGuardado` lleva el producto exacto (hasta 8 decimales) a centavos con el criterio de SQL Server al ajustar la
     escala (la mitad se aleja de cero), para que el valor en memoria sea el mismo que se relee. En USD se convierte con `trm_aplicada`
     (`PesosADolares`, centavos, la regla de todo precio en dólares), sin redondear antes el seguro a pesos.
   - `ActualizarSeguroPorValor`: si el valor de la mercancía cambió, toma la tasa vigente y recalcula; sin valor, el seguro sigue marcado
     sin tasa ni costo (la base lo admite: `incluyeSeguro = 1` con `costoSeguro` NULL).
   - `CopiarSeguro`: la versión nueva (`-vN`, de la re-solicitud o de la edición) copia el check, la tasa y el costo de la ruta
     equivalente y recalcula si cambió el valor, como el margen negociado.
2. **Precio.** `PrecioRutaCon` (la fórmula única de `RecalcularTotales` y de las vistas previas del margen, por ruta, en lote y del
   asistente) suma el seguro después del margen y de la venta de los conexos: `(fletes + retorno + levante) con margen + Σ venta de los
   conexos + seguro`. En USD el seguro se convierte con `trm_aplicada` (centavos). Sin transporte la ruta no tiene precio. `CostoFinal`
   no cambia: el seguro no es costo de Nexo. `PrecioTransporte` (tarifa del Excel y de la licitación) resta el seguro igual que los
   conexos.
3. **Endpoint.** `PUT /web/cotizaciones/{folio}/costeo/seguro` (`SeguroRutaWeb`: `incluye_seguro`, `linea_orden?`,
   `linea_solicitud_id?`), con el patrón de `PUT .../costeo/margen` (`clsSeguroCosteoService`): `If-Match` antes que la ruta, reglas del
   dominio (`ValidarCambioSeguro`), PDF invalidado e historial, versión de negocio, una escritura con control de versión y auditoría en
   `EventosHerramienta` (`cambiar_seguro_costeo`) **sin montos ni tasa**. Marcar una ruta que ya lo tiene responde 200 sin cambios.
4. **Lectura.** La tasa vigente la lee un repositorio de solo lectura (`IClsTarifaSeguroRepository`, la fila `isActive = 1`). El costeo
   expone `tasa_seguro_vigente` (en `GET .../costeo`, en el PUT del seguro, en las respuestas del margen y de los conexos y en el detalle por folio) y en cada ruta `incluye_seguro`, `tasa_seguro` y
   `valor_seguro` (en la moneda de la cotización).
5. **Nombre del campo: `valor_seguro`, no `costo_seguro`.** La issue pide `costo_seguro` en el GET; `clsCostoBrutoSinFugasTests`
   exige que el único campo `Costo*` de `RutaCosteo` sea `costo_final` (el costo de Nexo, #218). El seguro es venta (se le cobra al
   cliente sin margen), no costo de Botero: se publica como `valor_seguro` y la prueba de arquitectura no se relaja.
6. **Licitaciones fuera.** La HU no las menciona: el endpoint responde 409 `ESTADO_NO_PERMITE` con `detalle.motivo = LICITACION`.
7. **Recalcular por el valor de la mercancía** en los tres caminos que lo cambian: la edición en sitio o como reemplazo (HU-028,
   `clsEdicionSolicitud.TasaSeguroVigente`), la versión editada y la versión nueva de la re-solicitud con `datos_carga.valor_mercancia`
   (HU-013.4). La re-solicitud en el mismo folio no cambia el valor.
8. **Sin script nuevo.** Se registra el 20 en el arnés de pruebas y en `/health/ready` ("esquema incompleto: falta 20").
9. **Exportaciones (HU-045.3, #265).** El seguro sale como valor de venta (`valor_seguro`), nunca la tasa ni como costo
   (`clsCostoBrutoSinFugasTests` no se relaja; `clsSeguroSinTasaEnExportacionesTests` lo vigila):
   - **PDF** (NOGE0076: aparte del flete): la fila de la ruta imprime `precio_final_linea − valor_seguro` (`PrecioRutaPdf`) y una fila
     propia "Seguro de la mercancía" su valor (`ConFilasSeguro`, después de construir el documento, así la coherencia del payload sigue
     comparando la fila con su precio); en el pliego, el resumen agrega la fila del seguro debajo de la ruta. El total general no cambia.
   - **Excel de la cotización:** columna `Seguro de la mercancía ({moneda})` entre los conexos y el precio de la ruta, con su total; es
     venta, así que también va en la versión de solo lectura. **Excel de la licitación:** una quinta columna solo si alguna línea lo
     incluye (hoy ninguna: las licitaciones no lo admiten, y su archivo no cambia).
   - **Correo y vista previa** (HU-033, sin desglose): el precio de la ruta y el total ya lo incluyen; la ruta lo dice
     ("Incluye el seguro de la mercancía", `resumen.rutas[].incluye_seguro`) sin el monto.
   - **Detalle por folio:** `totales.precio_seguro` (B4 de la auditoría de #264): `precio_transporte + precio_conexos + precio_seguro =
     precio_final`.
   - **Check marcado sin valor de la mercancía** (decisión 7 de #264; decisión del usuario del 2026-11-09: **se avisa, no se bloquea**).
     El seguro no suma y el PDF sale sin su fila en esa ruta. `aviso_seguro` (`clsAvisoSeguro`, código `SEGURO_SIN_VALOR_MERCANCIA`, con
     las rutas de `clsCotizacionSolicitud.RutasConSeguroSinValor()`) va en `CosteoCotizacion` (y en el detalle) y en el 201 del PDF. El
     envío del correo tampoco se bloquea; la vista previa del correo trae el mismo aviso (B5). No toca Finalizar (#269).
   - **Centavos (B1, decisión del usuario):** el seguro se guarda en centavos (#264). En COP, el seguro y los montos que lo incluyen (precio
     de la ruta en el resumen del pliego y en el correo, totales) se muestran con 2 decimales cuando los tienen
     (`clsFormatoMoneda.FormatearExacto`; en el Excel, `#,##0.00` por celda), así lo impreso cuadra con lo guardado; sin centavos, en pesos
     enteros como siempre.

## Consecuencias

- **Métricas (M1 de la auditoría):** `margen_ponderado` y `ganancias` del tablero restan el seguro del precio en COP (es un traslado, la
  prima de la póliza, no margen); `valor_cotizado_cop` y `valor_ganado_cop` sí lo incluyen (se le cobra al cliente).

- El PDF, el Excel y el correo muestran el seguro aparte (HU-045.3, #265, §9). Un PDF generado antes de §9 imprime el precio de la ruta
  con el seguro dentro y sin su fila; se corrige al regenerarlo.
- Con el seguro marcado sin valor, la cotización puede salir sin el seguro que el comercial marcó: el aviso lo hace visible en el costeo
  antes de Finalizar y en la respuesta del PDF. Corregirlo después de finalizar exige una versión nueva.
- El seguro se puede marcar antes de que Nexo costee: queda guardado y suma cuando la ruta tenga precio.
- Una tasa vigente que no existe (tabla vacía) responde 500 `ERROR_INTERNO` al marcar: es un problema de configuración de la base.
- El historial del PDF invalidado usa el motivo `servicios_conexos`: el CHECK de `CotizacionPdfsHistorial.motivo` no tiene uno propio y
  agregarlo exige un script de Datos.

## Decisiones del usuario (2026-11-09)

1. **Sin redondeo** del seguro a pesos por ahora (se guarda en centavos, la escala de la columna). El precio de la ruta en COP puede
   tener centavos cuando hay seguro.
2. **Licitaciones fuera** por ahora (409 `ESTADO_NO_PERMITE`, `motivo = LICITACION`); se habilitan después.
3. El historial del PDF se queda con el motivo **`servicios_conexos`** (no se pide un motivo propio a Datos).
4. Seguro marcado sin valor de la mercancía (tras una edición): se **avisa**, no se bloquea; el aviso lo implementa HU-045.3 (#265).

## Pendientes (de decisión)

1. Nombre del campo en la API (`valor_seguro` frente a `costo_seguro` de la issue) y el de la base (`costoSeguro`, de Datos).
2. (Resuelta el 2026-11-09: licitaciones fuera por ahora.)
3. (Resuelta el 2026-11-09: el motivo sigue siendo `servicios_conexos`.)
4. ¿Marcar el seguro exige el costeo listo, como el margen? Hoy no (se puede marcar antes).
5. Una versión nueva (`-vN`) conserva el check; ¿debería volver a tomar la tasa vigente aunque no cambie el valor?
6. (§9) Resuelto el 2026-11-09: el check marcado sin valor de la mercancía se avisa, no se bloquea.
