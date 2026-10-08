# HU-013 — Preguntas para el equipo de Nexo y gaps del contrato de captura

| Campo | Valor |
|---|---|
| Fecha | 2026-10-05 |
| Issue | #97 (HU-013.1). Afecta a #99 (conexos), #100 (re-solicitud) y a la issue de gaps de catálogos que se abre a partir de esta |
| Decisión | ADR-018 (`backend-dotnet/specs/decisiones/ADR-018-pipeline-determinista-ia-etapa-final.md`) |
| Fuentes | Swagger de Nexo QA (`/swagger/v1/swagger.json`, leído solo con GET el 2026-10-05), `docs/conexion_nexo/HU-005.1-mapeo-campos-nexo.md` (mapeo verificado contra el código de Nexo), ADR-015 y el código de `backend-dotnet` |

Convención: **Confirmado** = está en el swagger o en el código de Nexo citado en el mapeo. **Inferido** = se deduce del swagger, pero el swagger no lo dice expresamente. **Abierto** = hay que preguntarlo.

## 1. Lo que ya se sabe por el swagger

- **Sin esquema de seguridad.** El swagger no declara `securitySchemes` ni `security`. La identidad viaja en el encabezado `X-Portal-Usuario-Id` (ADR-015), que el swagger tampoco documenta.
- **Roles en las descripciones.** Algunas operaciones los mencionan en su `summary`:
  - `POST /api/servicios-conexos` y `PATCH /api/servicios-conexos/{id}`: "solo Servicios Logísticos / Admin".
  - `POST /api/cotizaciones/{id}/resultado`: "Solo Comercial y Admin".
  - `GET /api/vehiculos`: "`[Authorize]` sin rol específico — mismo criterio que el resto de las consultas (`docs/ROLES_Y_PERMISOS.md`)".

  `POST /api/cotizaciones/{id}/conexos` y `GET /api/servicios-conexos` no mencionan rol.
- **`GET /api/servicios-conexos`.** Parámetros `soloActivos` (bool, por defecto `false`), `q`, `categoria`, `page` (≥ 1) y `pageSize` (1 a 200, por defecto 50). Responde `clsPaginaServiciosDto {items, total}`, y cada item es `clsServicioConexoDto`:
  - `id` (uuid), `codigoServicio`, `tipoServicio`, `unidad`, `vigencia` (date), `categoria` y `activo`;
  - `origen`, `destino`, `puerto`, `ruta` y `tipoMaquina`;
  - `costoUnitario`, que **Cotifly no expone**.

  `categoria` es `logistico` o `adicional` (descripción de `clsCrearServicioConexoRequest`).
- **`POST /api/cotizaciones/{id}/conexos`.** `{id}` es el uuid de la **cotización** de Nexo. El cuerpo es `clsAgregarConexosRequest {items: [clsConexoItemRequest]}`, con `minItems: 1`. Cada `clsConexoItemRequest` trae:
  - `lineaId` (uuid, **obligatorio**): "Línea de la cotización a la que aplica (S4-HU-15.4/#250) — obligatorio, ya no existe el conexo general sin línea";
  - `servicioConexoId` (uuid, **obligatorio**);
  - `cantidad` (0,01 a 999.999,99);
  - `valorAplicado` (opcional): "Si no se envía, se calcula como ValorBase * Cantidad del servicio".

  Respuestas: 200 `clsCotizacionDto`, 400 y 404 `ProblemDetails`.

  `lineaId` es el `id` de `clsCotizacionLineaDto`, la línea de la cotización. No es el `lineaSolicitudId`.
- **`GET /api/cotizaciones/{id}`.** "Consolidado/total de la cotización integral". Responde `clsCotizacionDto`:
  - `id`, `solicitudId`, `total`, `entregadaAt` y `esAutoCosteo`;
  - `estado`: `Borrador` o `Entregada`;
  - `resultado`: `SinDefinir`, `Ganada` o `Perdida`;
  - `lineas[]` de `clsCotizacionLineaDto`: `id`, `lineaSolicitudId`, `valorFlete`, `valorAprovisionamiento`, `valorRetornoContenedor` y `fuente` (`Sicetac`, `Historica`, `Manual` o `Tarifario`);
  - `conexos[]` de `clsCotizacionConexoDto`: `id`, `cotizacionLineaId`, `servicioConexoId`, `nombre`, `categoria`, `cantidad` y `valorAplicado`.

  Responde 404 si no existe.
- **`POST /api/captura-solicitudes`.** Recibe `clsCapturaSolicitudRequest` y responde 201 `clsCapturaResultDto {id, lineas, rutasCreadas, vehiculosCreados, scorePrioridad, fletesCalculados, lineasPorCostear, costeoEnProceso}` o 400. "Resolviendo o creando rutas y configuraciones de vehículo a partir del texto libre". No ofrece clave de idempotencia ni referencia externa.
- **`GET /api/solicitudes/{id}/propuesta-tarifa`.** "Calcula la propuesta para cada línea de la solicitud. No persiste nada". Responde `clsPropuestaSolicitudDto {solicitudId, lineas[clsPropuestaLineaDto], advertencias[]}`. Cada línea trae los candidatos `sicetac`, `plaza`, `historicoSap` y `retornoContenedor`, además de `valorPropuesto`, `fuentePropuesta`, `justificacion` y `vencida`. Es lectura sin efecto, pero da la **propuesta de la Biblioteca**, no el flete registrado en la cotización.
- **Otras operaciones relacionadas:**
  - `POST /api/cotizaciones` crea o recupera el borrador de una solicitud.
  - `POST /api/cotizaciones/{id}/entregar` responde 409 si ya estaba entregada.
  - `POST /api/solicitudes/{id}/rechazar` descarta la solicitud.
  - `POST /api/solicitudes/{id}/recalcular-score` recalcula solo el score de prioridad.
  - `POST /api/cotizaciones/{id}/aplicar-propuestas` precarga como flete el mayor valor propuesto en las líneas pendientes.

  **No hay** una operación para reabrir una solicitud ni para pedir de nuevo el costeo.

## 2. Preguntas

| # | Pregunta | Lo que ya se sabe | Estado |
|---|---|---|---|
| 1 | ¿`POST /api/cotizaciones/{id}/conexos` exige un rol? | **Confirmado por el código de Nexo, no por el equipo de Nexo** (`docs/conexion_nexo/HU-005.1-mapeo-campos-nexo.md`): `Comercial`, `CoordinadorFletes` y `Admin`. El swagger no lo declara. Cotifly traduce el 403 a `NEXO_SIN_PERMISO`. Falta confirmar que los comerciales de Cotifly tienen el rol `Comercial` en Nexo | Confirmado (rol); abierto (asignación a los comerciales) |
| 2 | ¿El conexo agregado queda costeado de inmediato o lo revisa Servicios Logísticos? | Inferido del swagger y del código de Nexo (no confirmado por el equipo de Nexo): sin `valorAplicado`, Nexo lo calcula como valor base × cantidad en la misma petición y devuelve la cotización con el conexo. No sabemos si Servicios Logísticos lo revisa o lo puede cambiar después, ni si el servicio tiene tarifas por ruta (`origen`, `destino`, `puerto` en el catálogo) que cambien el valor | Abierto |
| 3 | ¿Se pueden agregar conexos a una cotización `Entregada`? | El swagger no lo dice. Cotifly solo los agrega con el costeo listo (`autocosteo` o `entregada`) | Abierto |
| 4 | ¿Cómo se re-solicita el costeo sin cambios de carga (captura nueva o reabrir la solicitud)? | No hay endpoint para reabrir. Opciones: (a) una captura nueva con los mismos datos, que da otra solicitud y otra cotización; (b) un endpoint nuevo de Nexo para reabrir o recalcular; (c) `aplicar-propuestas` sobre el borrador, que no aplica a una cotización entregada. Cotifly propone (a) mientras Nexo no ofrezca (b). **Implementado (a) en #100:** la re-solicitud hace una captura nueva con lo guardado, guarda el id de la solicitud nueva y deja el id anterior en la auditoría. La solicitud anterior queda viva en Nexo (pendiente: ¿Nexo la descarta o la deja vencer?) | Abierto (para Nexo) |
| 5 | Con cambios de carga, ¿la captura nueva debe referenciar la solicitud anterior? | No hay campo para eso (solo `referencia` y `notas`). Cotifly crea la versión `-vN` y una solicitud nueva | Abierto |
| 6 | Autenticación de servicio para Cotifly | Hoy viaja `X-Portal-Usuario-Id` del comercial, sin token (ADR-015). El swagger no declara seguridad. ¿API key, JWT de servicio, allowlist de IP? | Abierto |
| 7 | Orden de las líneas: ¿`lineas[]` de `clsCotizacionDto` sigue el orden de las líneas de la solicitud? ¿Hay siempre exactamente una línea de cotización por línea de solicitud? | Cotifly traduce `linea_orden` → su `CotizacionLineasSolicitud` → `lineaSolicitudId` de Nexo → `clsCotizacionLineaDto.id`. No depende del orden del arreglo, pero sí de que haya una sola línea de cotización por línea de solicitud | Abierto |
| 8 | Orden de los conexos en la cotización (para el PDF) | `conexos[]` no tiene un campo de orden. Cotifly propone ordenar por el orden de agregado (o por `id`) | Abierto |
| 9 | Idempotencia de `POST .../conexos`: si se agota el tiempo y se reintenta, ¿se duplica el conexo? | No hay clave de idempotencia. Cotifly no reintenta los POST (ADR-015 §3) y relee la cotización antes de informar | Abierto |
| 10 | Mapeo de los estados de Nexo con `estado_costeo` | Vigente en el código y en ADR-018 §9: `Pendiente`/`EnCosteo` → `pendiente_costeo` o `autocosteo` según los fletes; `Entregada` → `entregada`; `Descartada` → `descartada`. Falta confirmar con Nexo que `EnCosteo` no implica que un coordinador la esté trabajando a mano (Cotifly no los distingue) | Abierto (confirmación) |
| 11 | ¿Hay un endpoint de lectura del estado de la solicitud sin efecto? | `GET /api/solicitudes/{id}` la pasa de `Pendiente` a `EnCosteo` (ADR-015) | Abierto (heredado de ADR-015) |
| 12 | Catálogo de carrocería | No hay `/api/catalogos/carrocerias`. ¿Lo van a publicar? | Abierto |
| 13 | ¿Existe DELETE de conexos? | El swagger no tiene `DELETE /api/cotizaciones/{id}/conexos/{conexoId}`. Sin eso, excluir un conexo en Cotifly (`PUT .../servicios-conexos`) deja el total de Nexo distinto del PDF de Cotifly | Abierto |
| 15 | (Negocio) Un `deadline` con fecha sola (`2026-10-10`): ¿es el inicio del día (00:00 de Bogotá, lo que hace Cotifly hoy) o el fin del día (23:59:59)? | HU-013.10 (G4) lo toma como 00:00 de Bogotá y lo documenta en el contrato | Abierto (negocio) |
| 14 | ¿Agregar un conexo cambia el estado de la cotización (`Borrador`/`Entregada`) o su `entregadaAt`? | El swagger no lo dice. Si la devuelve a `Borrador`, el polling de Cotifly pasaría de `entregada` a `autocosteo` | Abierto |

Las respuestas de las preguntas 1 y 2 salen del código de Nexo (`docs/conexion_nexo/HU-005.1-mapeo-campos-nexo.md`) y del swagger. Falta que el equipo de Nexo las confirme.

**Nota sobre el mapeo:** la sección de servicios conexos de `docs/conexion_nexo/HU-005.1-mapeo-campos-nexo.md` (línea 114) describe el cuerpo de `POST /api/cotizaciones/{id}/conexos` como `{ items: [{ servicioConexoId, cantidad?, valorAplicado? }] }`. Omite `lineaId`, que el swagger exige en `clsConexoItemRequest` (obligatorio, junto con `servicioConexoId`). El documento copiado no se cambia, y el contrato de Cotifly sigue al swagger.

## 3. Gaps del contrato de captura (mapeo ↔ DTO de Cotifly ↔ swagger de Nexo)

**Estado (2026-10-05, HU-013.10, #106):** resueltos en el backend G1, G2, G3, G4, G5 (formulario de una línea; en licitaciones
viajan por línea las horas del encabezado), G6 (formulario), G7, G11 (advertencia en el log), G12, G14 (`clienteId` para
prospectos con id en el catálogo), G15 y G16 (los `CLIENTE_*` quedan reintentables: el reintento vuelve a resolver el BP).
G4 deja la fecha sola como inicio del día en Bogotá (pregunta 15 para negocio). G13 y G17 no requieren cambio de captura en el backend (G13 es diseño coherente
con ADR-015; G17 lo implementa #99). Quedan para el frontend y negocio G8 y G9, y G10 es de #99. Pendiente: confirmar con
Nexo si `clases-contenedor` devuelve los códigos `CNT..` en `descripcion` (Cotifly acepta los códigos en código y, además,
las descripciones de ese catálogo).

**Estado (2026-10-07, HU-013.11, #108):** G2, G3, G5 y G6 quedan cerrados también en las licitaciones (`POST /web/licitaciones`).
Cada fila del Excel se valida por código de catálogo con el mismo `IClsValidadorCatalogosCaptura` del formulario (sin duplicar
reglas; `CODIGO_NO_CATALOGO` por campo) y con las reglas condicionales de G3 (una sola regla en
`clsLineaSolicitudCosteo.ErroresCondicionales`, que también usa el formulario). Todos los errores vuelven juntos en `errores[]`.
Las horas viajan por línea (`horas_cargue`/`horas_descargue` opcionales en `LineaLicitacionWeb`, guardadas en el script 0008;
sin ellas, las del encabezado). Los ids de municipio viajan por línea: la fila puede traerlos (`origen_municipio_id`/
`destino_municipio_id`) y, si no, Cotifly resuelve el texto con el catálogo de `GET /web/municipios` y la regla determinista de
HU-005.13; si es ambiguo (`MUNICIPIO_AMBIGUO`) o no aparece (`MUNICIPIO_NO_ENCONTRADO`) es un error de la fila, nunca se elige un
candidato. Sin catálogo maestro: 503 `CATALOGOS_FLETE_NO_DISPONIBLE` (copia de hasta 24 h, como el formulario); sin catálogo de
municipios cuando alguna fila lo necesita: 400 `MUNICIPIOS_NO_DISPONIBLE`. Nada se guarda en esos casos.

Comparación de `docs/conexion_nexo/HU-005.1-mapeo-campos-nexo.md` (Parte 2) con `clsSolicitudCosteoWebRequest`, `clsPosicionCargaWebRequest`, el envío `clsCosteoNexo.CuerpoCaptura` y `clsCapturaSolicitudRequest`, `clsCapturaLineaRequest` y `clsCapturaPosicionRequest` del swagger. **Ningún campo de la Parte 2 falta en el DTO ni en el spec.** Todos están, con los mismos límites de Nexo. Los gaps son de comportamiento y de catálogos: **17 en total (G1 a G17)**. No se implementan en #97, que es solo SDD; van a una issue nueva.

| # | Gap | Dónde | Propuesta |
|---|---|---|---|
| G1 | `/web/catalogos-flete` lee `GET /api/biblioteca-sap-historico/filtros` (histórico de tarifas), no los catálogos maestros | `clsCatalogosFleteNexo` | Migrar a `GET /api/catalogos/tipos-vehiculo` (`{codigo, descripcion, esCatalogoOficial}`), `clases-viaje` (`{id, descripcion}`) y `clases-contenedor` (`{id, descripcion, tamanoContenedor, pesoInfCnt, pesoSupCnt}`) sin cambiar la respuesta `CatalogosFlete`. La carrocería sigue en código y la tipología es un enum de 7 valores |
| G2 | `tipo_vehiculo`, `carroceria`, `clase_viaje`, `clase_contenedor` y `tipologia` solo se validan por largo. Nexo resuelve texto libre y **crea un tipo de vehículo nuevo** si no hay coincidencia (duplicados) | `clsSolicitudCosteo.Validar` | Validar contra el catálogo (G1) o el enum antes de guardar y responder `SOLICITUD_COSTEO_INVALIDA`. **No rechazar** la lista provisional de Nexo para extradimensionado (`CNT20.FT`, `CNT20.OP`, `CNT40.FT`, `CNT40.OP`), que no viene en el catálogo maestro. Escenarios en `catalogos_formulario_costeo.feature`. **Cerrado** en el formulario (#106) y en las filas de una licitación (#108, `licitaciones_excel.feature`) |
| G3 | `clase_contenedor` debería ir solo con `clase_viaje = Contenedor`, y `valor_aprovisionamiento` solo con un levante distinto del origen (Nexo lo descarta en otro caso). No hay validación condicional | `clsSolicitudCosteo.Validar` | Rechazar la combinación inválida, o descartar el valor con una advertencia. **Cerrado**: se rechaza en el formulario (#106) y en cada fila de una licitación (#108, `CAMPO_REQUERIDO`/`CAMPO_NO_APLICA`) |
| G4 | `deadline`: el DTO es `DateTimeOffset` y se reenvía en ISO 8601, pero una fecha sola o sin zona (`2026-10-10`) toma la zona del servidor (UTC en el contenedor): 19:00 del día anterior en Bogotá | `clsSolicitudCosteoWebRequest` / serialización | Interpretar las fechas sin zona en America/Bogota, o exigir zona. Documentado en el spec |
| G5 | `horasCargue`/`horasDescargue` por línea: Nexo los acepta en la línea (sobreescriben el valor por defecto); Cotifly solo los envía en el encabezado | `clsCapturaLineaWire` | No bloqueante con una línea. Para licitaciones, agregarlos a `LineaLicitacionWeb`. **Cerrado** (#108): opcionales por fila, guardados (0008) y reenviados; sin ellos viajan los del encabezado |
| G6 | `origenMunicipioId`/`destinoMunicipioId`: Nexo los acepta (id de `tbl_municipios` del buscador) y `GET /web/municipios` ya devuelve `id`, pero Cotifly no los envía | `SolicitudCosteoWeb`, `clsCapturaLineaWire` | Agregar `origen_municipio_id` y `destino_municipio_id` opcionales y reenviarlos (evita la resolución aproximada de la ruta). **Cerrado**: formulario (#106) y licitaciones (#108: opcionales por fila o resueltos por Cotifly desde el texto; ambiguo o sin coincidencia = error de la fila) |
| G7 | `notas` (encabezado de Nexo, hasta 2000) no se envía | `clsCapturaSolicitudWire` | Decisión de HU-005.1: la personalización es del PDF. Confirmar con negocio si conviene mandar una nota a la Mesa |
| G8 | `tipologia` por posición: el wizard del frontend la captura por mercancía y Nexo solo la tiene por línea | Frontend (`cotizador.component.ts`) | El contrato la deja en la línea. El frontend la mueve a la línea o la retira (decisión de negocio) |
| G9 | Formulario del frontend sin `referencia`, `valor_estimado` ni `valor_aprovisionamiento` | Frontend | El backend ya los acepta. Agregarlos al wizard (HU-013.8) |
| G10 | Los servicios conexos solo se leen (`LeerCotizacion`); no existe la llamada a `POST /api/cotizaciones/{id}/conexos` | `clsCosteoNexo` | La implementa HU-013.3 (#99) con `lineaId` |
| G11 | `MargenesPolitica.mapeoLineasNexo` no se valida contra `GET /api/catalogos/lineas-negocio` | Administración de líneas | Validar al guardar el mapeo en `PUT /web/admin/lineas-negocio/{id}` |
| G12 | `docs/docs_front/CONTRATO_API_WEB.md` no documenta `POST /web/solicitudes-costeo` como el endpoint de creación | Documentación | Actualizar en HU-013.8 |
| G13 | `/web/solicitudes-costeo` acepta una sola línea por llamada (el wizard hace una llamada por línea) | Diseño | Es coherente con ADR-015. Para varias líneas está `POST /web/licitaciones` |
| G14 | Resolución del cliente: Nexo exige `clienteId` (uuid) o `clienteBp`, nunca el NIT. Cotifly busca el NIT en el catálogo de Nexo y envía solo `clienteBp`. Un prospecto sin BP termina en `error_envio` (ADR-015) | `clsEnvioCosteoService`, `clsCosteoNexo` | Enviar `clienteId` cuando el buscador lo devuelva (lo necesitan los prospectos), y definir el flujo del prospecto con Nexo |
| G15 | Lista provisional de clases de contenedor (`CNT20.FT`, `CNT20.OP`, `CNT40.FT`, `CNT40.OP`, extradimensionado nacional), marcada como provisional en el código de Nexo | Catálogo de `clase_contenedor` | Aceptarla junto con el catálogo maestro hasta que Nexo la formalice (ver G2) |
| G16 | Los errores 400 de la captura de Nexo llegan como un único `COSTEO_NEXO_SOLICITUD_INVALIDA` con el código de Nexo en el motivo | `clsCosteoNexo` | Traducir a códigos de Cotifly con `detalle.motivo` = el de Nexo: `ENUM_INVALIDO` y `LINEA_INCOMPLETA` → `SOLICITUD_COSTEO_INVALIDA` (catálogo o línea; validarlos antes con G2 y G3); `PESO_REQUERIDO` y `PESO_INCONSISTENTE` → `SOLICITUD_COSTEO_INVALIDA` (ya se validan antes de enviar); `CLIENTE_REQUERIDO`, `CLIENTE_INVALIDO` y `CLIENTE_BP_INVALIDO` → `COSTEO_NEXO_SOLICITUD_INVALIDA` con un motivo de cliente (revisar el BP en SAP/Nexo); `DWCUBOS_NO_CONFIGURADO` → `COSTEO_NEXO_NO_DISPONIBLE` (configuración de Nexo, reintentable). Todos dejan `error_envio` con el motivo legible |
| G17 | El cuerpo de conexos del mapeo (HU-005.1, línea 114) omite `lineaId`, que el swagger exige | `docs/conexion_nexo/HU-005.1-mapeo-campos-nexo.md` | El contrato de Cotifly sigue al swagger (`linea_orden` → `lineaId`). #99 guarda el id de la línea de la cotización de Nexo en `clsLineaCosteoNexo` |
