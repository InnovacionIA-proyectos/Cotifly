# ADR-031 — Conexos que no encuentran su línea en Nexo: destino estable y salida definitiva (#259)

- **Estado:** Propuesta (2026-11-06). Issue #259. Variante 4a decidida por el usuario el 2026-11-06; el resto, pendiente del visto bueno
  del PR.
- **Relacionadas:** ADR-019 decisiones 3 y 10 (conexos pegados al primer camión vigente, envío diferido), ADR-026 (conexos por lote por
  el envío diferido), HU-036.2 y #228 (finalizar con `conexos_pendientes_en_nexo`), HU-028.1 (#183, camión rechazado).

## Contexto

El envío diferido (decisión 10) manda los conexos de cada ruta a la línea de la cotización de Nexo del primer camión vigente
(decisión 3) la primera vez que el polling la ve. Si no la ve, espera: "la cotización que el polling sigue consultando lo intenta cuando
la línea aparezca". Esa espera no tenía salida:

1. `LineaDeNexo` exigía **exactamente una** línea de la cotización con el `lineaSolicitudId` del camión, mientras el costo
   (`CostoLinea`, `CostosCamion`) **suma** todas las que haya. Con la línea de un camión partida en dos por la Mesa hay precio, pero el
   conexo nunca tiene destino. Reproducido el 2026-11-06 con Testcontainers y el Nexo falso (es el mecanismo más probable del caso real
   `PCN-900017447-DG-22102026-002`; falta verlo en NEXO).
2. Al entregar, el polling deja de consultar la cotización. El envío salía con "nada" (sin registro y sin reintento pendiente), la
   cotización salía de la agenda y el reintento de las entregadas solo entraba con un registro `fallido` o `en_espera`.
3. Finalizar (#228) relee Nexo y solo confirma lo que Nexo ya tiene: el conexo quedaba `pendiente` y "Finalizar" respondía 409
   `conexos_pendientes_en_nexo` para siempre. Por lote (ADR-026) quedaba un `en_espera` sin agenda, que tampoco llegaba a su tope.

En una cotización entregada la situación ya no cambia: reintentar más veces no basta, el envío necesita un destino o una salida.

## Decisión

1. **"Tener línea" es tener al menos una.** `LineaDeNexo` toma, de las líneas de la cotización de Nexo con el `lineaSolicitudId` del
   camión, la **primera por id**: un criterio estable, el mismo en el envío diferido y en el `POST` síncrono de
   `POST .../costeo/servicios-conexos`. **"Primera" es orden ordinal del texto del id, no cronológico** (auditoría B5): no equivale a
   "la primera que creó la Mesa"; con ids GUID la elección es arbitraria pero determinista, y con ids numéricos "10" va antes que "9". Lo
   que pide la decisión es que siempre se elija la misma; falta confirmar con el folio real que la Mesa parte líneas así.
   **La única línea de la cotización** (`LineaDeNexo` sin camión, la regla anterior) **solo se usa para una ruta sin camiones**: una ruta
   cuyos camiones están todos descartados o rechazados no tiene destino (antes podía tomar la línea de otra ruta y cobrar allí el
   conexo con un `POST` no idempotente; auditoría M2, validación H1). En estado terminal esa ruta queda como dice el punto 2.
2. **Variante 4a (decisión del usuario).** Si el primer camión vigente no tiene línea y la cotización **ya no se costea** en Nexo
   (`SeConsultaEnNexo` falso: entregada, descartada, vencida), los conexos van a la línea del **primer camión vigente que sí la tiene**
   (un camión entregado sin flete se trata como uno rechazado). Si ningún camión de la ruta tiene línea, quedan `fallido_definitivo` con
   warning y el aviso "Revísalos en Nexo" del costeo: no bloquean finalizar, igual que los demás definitivos. Mientras la cotización se
   costea se mantiene la espera. En estado terminal el envío nunca sale "en silencio".
   **Entregada sin precio** (auditoría B1, decisión del usuario): una entregada con un camión sin flete se trata como terminal aunque el
   polling todavía la vaya a releer (`RequiereRelecturaCosteo`, HU-013.14). La 4a (o el definitivo) se aplica en la misma consulta que
   entrega, sin esperar la relectura: "un camión entregado sin flete se trata como uno rechazado".
   **Rutas rechazadas** (auditoría M2): el envío diferido y el reintento no recorren las rutas rechazadas por la Mesa, el mismo alcance
   que #228 en finalizar y que el invariante del punto 4. Sus conexos no se envían ni quedan `fallido_definitivo` (el aviso "el precio ya
   los incluye" sería falso en una ruta sin precio): siguen como estaban y no bloquean finalizar.
3. **El reintento ve los nunca intentados.** `TieneReintentosAsync` cuenta también los conexos sin registro cuando la cotización ya tiene
   cotización de Nexo; si Nexo no responde la lectura del reintento, quedan `en_espera` (con el tope de
   `CosteoNexo:EsperaMaximaCatalogoConexosHoras`) en vez de quedar sin registro.
4. **Invariante de agenda.** El polling no saca de la agenda una cotización (con cotización de Nexo, dueño y no perdida solo en Cotifly)
   mientras tenga conexos `pendiente` en sus rutas cotizables: ni en el ramal de reintento ni después del envío de la consulta que
   entrega, tampoco si ese envío lanzó una excepción. Con los puntos 2 y 3 eso converge siempre en la vuelta siguiente: se envía o queda
   definitivo. **Tras una excepción del envío** (auditoría B3) se agenda con la actualización condicional del punto 5 (sin la unidad de
   trabajo, así no se persiste lo que el envío dejó a medias) y con techo: solo mientras la solicitud tenga menos de
   `CosteoNexo:EdadMaximaSincronizacionResultadoDias` desde su envío a Nexo. Una excepción determinista no reagenda sin fin; pasado el
   techo, la cotización queda fuera de la agenda con un warning y finalizar la vuelve a encolar.
5. **Finalizar reencola, sin POST.** Si después de releer Nexo (o sin poder releerla) siguen pendientes, finalizar responde el mismo 409
   y vuelve a poner en la agenda, para ahora, una cotización que está **fuera** de ella. El envío lo hace el worker bajo su reclamo: no
   hay doble `POST` (que no es idempotente). Así se reparan solas, al primer clic, las cotizaciones que ya quedaron atascadas: no hace
   falta script de datos.
   - **Actualización condicional** (auditoría M1): `IClsCotizacionRepository.ReencolarConsultaCosteoAsync` es un `ExecuteUpdateAsync`
     con `WHERE id = @id AND costeoSiguienteConsultaAt IS NULL`, como `ReclamarConsultaCosteoAsync`. La decisión no depende de la
     cotización en memoria (leída antes de releer Nexo, que tarda segundos): un doble clic, o finalizar junto con el lote, nunca pisa un
     reclamo vigente del worker. El lote de ADR-026 conserva su patrón (agenda con la unidad de trabajo y holgura de dos reclamos, sin
     una llamada a Nexo de por medio); llevarlo al mismo método queda para una issue aparte.
   - **Solo fuera de la agenda** (auditoría B2): una cotización ya agendada (reclamada o esperando el siguiente reintento, 5, 15 o 60 min)
     no se mueve. Varios clics no adelantan la espera de los reintentos ni agotan `CosteoNexo:MaximoReintentosConexos`; el 409 basta.

6. **El `POST` individual no aplica la 4a** (validación H2). `POST .../costeo/servicios-conexos` (síncrono, del comercial) sigue
   exigiendo la línea del primer camión vigente: sin ella responde `LINEA_SIN_COTIZACION_NEXO`. El lote (ADR-026) sí la aplica, porque
   va por el envío diferido. La diferencia es casi inalcanzable (sin la línea del primer camión no suele haber precio y ninguno de los
   dos admite conexos sin costeo) y alinearlos movería la validación previa del `POST`; queda así, a la vista.

## Alternativas consideradas

- **Solo contar los conexos sin registro en el reintento** (opción 1 del análisis): hace entrar al reintento, pero sin destino vuelve a
  salir con "nada" y la cotización deja la agenda.
- **Registrar `en_espera` cuando falta la línea** (opción 2): deja rastro, pero el reintento vuelve a salir con "nada" y detiene el
  polling (es el camino del lote).
- **Que finalizar envíe los conexos** (opción 3): sin el reclamo del worker puede cruzarse con un reintento y duplicar un `POST` no
  idempotente; además mete la latencia y los fallos de Nexo en el clic.
- **Mantener "exactamente una" línea** y resolver solo la salida: con la línea partida el conexo quedaría `fallido_definitivo` aunque
  el camión sí esté en Nexo.

## Consecuencias

- Un conexo de una cotización entregada siempre termina `enviado`, `incierto` o `fallido_definitivo`; `pendiente` es transitorio.
- Contrato sin cambios: mismos estados, códigos y campos. Cambia el comportamiento visible: el 409 `conexos_pendientes_en_nexo` ya no es
  permanente (reintentar "Finalizar" en unos segundos) y `fallido_definitivo` aparece también por "sin línea" (openapi y
  `CONTRATOS_API_COTIFLY_FRONTEND.md`, fila 76).
- Una consulta más a `EventosHerramienta` (estados del envío) solo cuando la cotización estaba por salir de la agenda.
- **Riesgo aceptado:** con la variante 4a, si la Mesa vuelve a costear después el primer camión de una entregada, los conexos ya
  quedaron en la línea de otro camión de la misma ruta (la ruta y el precio no cambian).
- **Riesgo aceptado:** en una cotización descartada o vencida con líneas en Nexo, el envío diferido sigue intentando el `POST` a una
  línea vigente, como antes de este cambio; si Nexo lo rechaza, termina `fallido_definitivo` por el tope.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Confirmar en NEXO que la cotización de `PCN-900017447-DG-22102026-002` tiene más de una línea con el `lineaSolicitudId` del primer camión (consulta de solo lectura del análisis de #259) | Duban Guerra |
| Confirmar con NEXO el formato de los ids de las líneas de la cotización (validación H3): "primera por id" es orden ordinal del texto, así que con ids numéricos `CL-10` va antes que `CL-9` | Duban Guerra |
| Visto bueno del PR (puntos 1, 3, 4, 5 y 6; el 2 ya está decidido) | Usuario |
| Opcional: barrido al arrancar el worker que reencole las entregadas con conexos pendientes y sin agenda, si hace falta repararlas antes de que el comercial vuelva a finalizar | Usuario |
