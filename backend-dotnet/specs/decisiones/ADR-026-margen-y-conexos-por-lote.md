# ADR-026 — Margen y servicios conexos para varias rutas en una operación (HU-041.1)

- **Estado:** Propuesta (2026-10-27). Issue #231 (HU-041.1), milestone HU-041. Pendiente del visto bueno del usuario (ver Pendientes).
- **Relacionadas:** ADR-015 (costeo con Nexo), ADR-018 §3, §4, §8, §11 y §13 (margen, conexos, `If-Match`, regla del PDF, reserva de la
  versión), ADR-019 decisiones 3, 6 y 10 (conexos y margen por ruta, envío diferido de conexos), HU-036 (cotización finalizada).

## Contexto

Con cotizaciones de varias rutas el comercial aplica el mismo margen, vuelve al sugerido o agrega y quita el mismo servicio conexo ruta
por ruta: N llamadas a `PUT .../costeo/margen` o a `POST`/`PUT .../costeo/servicios-conexos`, cada una con su `If-Match`, su
invalidación del PDF y su entrada de auditoría. Si una falla a mitad, la cotización queda con unas rutas cambiadas y otras no. El
frontend (HU-041) pide hacerlo en una sola operación, atómica, e identificar el mismo conexo entre rutas sin comparar por nombre.

El problema real está en los conexos: el agregado por ruta (`POST .../costeo/servicios-conexos`) llama de forma síncrona al
`POST /api/cotizaciones/{id}/conexos` de Nexo, que no es idempotente y no tiene DELETE. N llamadas síncronas a Nexo (o una sola con
varias líneas) no se pueden deshacer si Cotifly falla después: no hay forma de que "todo o nada" abarque a Nexo.

## Decisión

1. **Tres endpoints nuevos, explícitos; los de una ruta no cambian.**
   - `PUT /web/cotizaciones/{folio}/costeo/margen/lineas` (`MargenLineasWeb`): `lineas` (los `linea_orden`) o `todas: true`, y `margen`
     o `revertir_sugerido: true`.
   - `POST /web/cotizaciones/{folio}/costeo/servicios-conexos/lineas` (`ConexoLineasWeb`): `lineas` o `todas`, `servicio_id` del
     catálogo y `cantidad`.
   - `POST /web/cotizaciones/{folio}/costeo/servicios-conexos/lineas/quitar` (`QuitarConexoLineasWeb`): `lineas` o `todas` y
     `servicio_id`. Se descartó `DELETE` con cuerpo (OpenAPI 3.0 no le da semántica y varios clientes lo descartan) y `DELETE` con la
     lista en la query (la selección tendría dos formas según el verbo); el sufijo verbal sigue la convención de la API
     (`.../reintentar`, `.../re-solicitar`, `.../finalizar`).
   Las tres responden `CosteoCotizacion` (la misma vista que `GET .../costeo`, con `estado_nexo`), exigen `If-Match` y aceptan
   `Idempotency-Key`. Caso de uso único: `clsCosteoLineasService`.
2. **Las mismas reglas que N llamadas por ruta, validadas antes de tocar nada.** Se reutilizan las reglas del dominio y de los casos de
   uso por ruta (`ValidarAjusteMargen`, `clsAjusteMargenCosteoService.Recalcular`, `clsServiciosConexosCosteoService.ValidarPrecondiciones`,
   `clsReglaPdfCosteo`, `ValidarRutaNoRechazada`). Orden: forma (400 `ErrorWeb`), dueño (404), `If-Match` (409), reglas de la cotización
   (licitación, costeo listo, `PDF_YA_ENVIADO` con la versión nueva, que también cubre la cotización finalizada de HU-036.3) y después ruta
   por ruta. La primera ruta que no admite el cambio corta la operación: **ninguna ruta cambia** y el error lleva
   `detalle.linea_orden` y `detalle.linea_solicitud_id` (sin códigos de error nuevos). Solo si todas pasan se aplican los cambios y se
   guardan en **una** escritura con el control de versión (`GuardarCambiosSiVersionAsync`): una carrera con otra mutación da 409
   `CONFLICTO_CONCURRENCIA` y nada queda a medias.
3. **Selección.** `lineas` de 1 a 1000 (el tope de rutas de una cotización), sin repetir; `todas` son las rutas cotizables (las rechazadas
   por la Mesa no cuentan; sin ninguna, 409 `SIN_RUTAS_COTIZABLES`). Una ruta pedida explícitamente que la Mesa rechazó es 409
   `RUTA_RECHAZADA` (igual que por ruta). Una ruta que ya está como se pide (mismo margen, ya tiene el conexo, no lo tiene) no cambia; si
   ninguna cambia, 200 sin invalidar el PDF ni subir la versión (idempotente, como el mismo margen por ruta).
4. **Revertir al sugerido.** Cada ruta vuelve a su `SugeridoVigente` y `origen_margen` vuelve al del sugerido (`sap_tm` o
   `politica_linea`) con `clsLineaSolicitud.RestaurarMargenSugerido`: deja de figurar como negociada. No se registra en `MargenAjustes`
   (no es una negociación y no debe contar en las métricas de ajustes); queda en la auditoría. Una ruta sin sugerido: 400
   `AJUSTE_MARGEN_INVALIDO` con la ruta.
5. **Conexos y Nexo: outbox, nunca N POST síncronos.** Agregar en lote NO llama al `POST` de conexos de Nexo dentro de la petición. El
   conexo se guarda en cada ruta con el **costo del catálogo de Nexo** (`IClsCostosConexosNexo`, leído antes de cambiar nada: Nexo caído
   es 503 sin cambios), igual que un conexo capturado con la ruta (decisión 3 de ADR-019), con el id del catálogo como
   `idServicioConexo` e `idServicioConexoCatalogo`. En la MISMA transacción queda, por ruta, el registro `en_espera` del envío diferido
   (`clsEnvioConexosNexo`, ADR-019 decisión 10) y, si el polling estaba detenido (cotización entregada), la cotización vuelve a la agenda
   solo para ese envío (`ProgramarRelecturaCosteo`; no se mueve una consulta ya agendada para no cruzarse con una réplica que la
   reclamó). El envío diferido ya resuelve lo difícil: una sola vez por solicitud de Nexo, a la línea del primer camión vigente, registro
   `en_curso` antes del POST, relectura de un POST incierto, tope de reintentos y estados visibles (`pendiente`, `enviado`, `incierto`,
   `fallido_definitivo`). Mientras un conexo siga `pendiente`, finalizar responde 409 `COTIZACION_NO_FINALIZABLE`
   (`conexos_pendientes_en_nexo`, #228), así que el precio nunca se acepta con un conexo que Nexo no tiene. Quitar en lote, como el `PUT`
   por ruta, borra la fila en Cotifly y no toca Nexo (no hay DELETE).
   - *Alternativa descartada:* un solo `POST` a Nexo con una línea por ruta (Nexo lo aplica todo o nada). Seguía siendo una escritura
     externa no reversible dentro de la petición: si Cotifly fallaba después (conflicto de versión, caída), quedaban conexos en Nexo que
     Cotifly no tiene, y los conexos nuevos de Nexo no siempre dicen su línea (#134).
   - *Consecuencia:* el precio del conexo en lote es el costo del catálogo, no el `valorAplicado` que Nexo calcula al agregarlo por ruta
     (los dos salen del mismo valor base del catálogo; es el mismo criterio de los conexos capturados).
6. **Una sola invalidación del PDF y una sola auditoría por operación.** `clsReglaPdfCosteo.InvalidarPdf` se llama una vez (un registro
   en el historial del PDF) y cada operación deja una entrada en `EventosHerramienta` (`ajustar_margen_costeo_lineas`,
   `agregar_servicio_conexo_lineas`, `quitar_servicio_conexo_lineas`) con las rutas cambiadas y sin montos; el error también se audita.
   El margen negociado sigue dejando una fila de `MargenAjustes` por ruta ajustada (es el dato de negocio, como por ruta) y los registros
   `en_espera` del envío diferido son su estado operativo (herramienta `conexos_envio_nexo`), no auditoría.
7. **`servicio_id` en `ServicioConexoCosteo`.** Es `idServicioConexoCatalogo` (#188): el id del catálogo, el mismo de
   `GET /web/servicios-conexos` y el que piden los endpoints en lote. `id` sigue siendo el de la fila (puede ser el de la cotización de
   Nexo). `null` en filas anteriores a #188.
8. **Idempotencia.** `Idempotency-Key` opcional con el almacén persistente (`IClsAlmacenIdempotencia`): solo los éxitos consumen la clave;
   la repetición devuelve la respuesta guardada sin ejecutar ni auditar otra vez; la misma clave con otro cuerpo es 409
   `IDEMPOTENCY_KEY_REUTILIZADA`. Sin clave, repetir responde 200 sin cambios (punto 3).

## Consecuencias

- El frontend aplica el margen, vuelve al sugerido o agrega y quita un conexo en todas las rutas con una llamada y un `If-Match`; un
  fallo no deja la cotización a medias y dice qué ruta lo causó.
- Un conexo agregado en lote se ve con precio de inmediato pero queda `pendiente` hasta el siguiente ciclo del polling (como uno
  capturado). En una cotización entregada esto vuelve a agendarla solo para el envío.
- Sin script de base de datos ni configuración nueva; sin códigos de error nuevos.

## Pendientes

- Visto bueno del usuario sobre: (a) que agregar en lote use el envío diferido (precio del catálogo y `pendiente` unos segundos) en vez
  del `POST` síncrono por ruta; (b) que revertir al sugerido no deje fila en `MargenAjustes`; (c) que `todas` omita las rutas
  rechazadas y que las rutas que ya están como se pide no cuenten como bloqueo.
