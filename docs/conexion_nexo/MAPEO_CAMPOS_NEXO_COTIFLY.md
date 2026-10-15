# Mapeo de campos Nexo ↔ Cotifly (HU-005.1)

Documento de referencia para la integración entre el formulario "Nueva cotización" de Cotifly y el contrato de captura de solicitudes de Nexo. Contiene dos partes: el mapeo original aprobado al diseñar el formulario (2026-09-26) y una actualización (2026-10-05) que verifica cada campo contra el código fuente real de los tres repositorios involucrados.

---

## Parte 1 — Mapeo original (aprobado con negocio, 2026-09-26)

Tabla campo por campo entre el formulario "Nueva solicitud" de Nexo (`administracion_embarqa_nexo`, módulo `cabina-fletes`) y el flujo de cotización de Cotifly, construida contra el contrato documentado de Nexo (`CONTRATO.md`) y el esquema de Cotifly vigente en ese momento. Fue el insumo de HU-005.1 y la base para el diseño del formulario en HU-005.3.

### Tabla de mapeo

| Campo de Nexo | Equivalente en Cotifly | Tipo | Catálogo / opciones | Clasificación |
|---|---|---|---|---|
| `tipo` (spot / licitación) | No existía | string enum | `Spot`, `Licitacion` | Nuevo — confirmado con negocio: aplica igual |
| `modalidad` | No aplica | string enum | `Importacion`, `Exportacion`, `Nacional`, `Extradimensionada`, `CargaGeneral` | No aplica — la línea de negocio ya cubre esta necesidad; sería redundante |
| `deadline` | No existía | fecha | — | Nuevo |
| `horasCargue` / `horasDescargue` | No existía | decimal | — | Nuevo |
| `referencia` | No existía | string | — | Nuevo |
| `valorEstimado` (encabezado, uno por solicitud) | No existía | decimal | — | Nuevo |
| `lineaNegocio` (por línea) | `cotizaciones.linea_negocio_id` → `MargenesPolitica` | FK a catálogo propio | Nexo: `PuertoAInterior`, `InteriorAPuerto`, `Nacional`, `DevolucionContenedor`, `Andino`, `Licitaciones` | Resuelto — `MargenesPolitica.mapeoLineasNexo` (HU-002.4 Fase 2) |
| `origen` / `destino` | Texto libre | string → catálogo | `GET /api/captura-solicitudes/municipios` (columna `municipio`), autocompletado simple | Modificar — deja de ser texto libre |
| `tipoVehiculo` | Texto libre | string enum | SICE-TAC: `2`, `2_5`, `2_8`, `2L1`, `2L2`, `2L3`, `2S2`, `2S3`, `3`, `3S2`, `3S3`, `CA`, `V2`, `V3`, `V4` | Modificar — hardcodeado mientras Nexo no exponga endpoint propio; texto amigable en frontend |
| `carroceria` | No existía | string enum | `ESTACAS`, `ESTIBAS`, `FURGON`, `FURGON REFRIGERADO`, `PLATAFORMA`, `PORTACONTENEDORES`, `TANQUE`, `TRAYLER`, `VOLCO` | Nuevo, hardcodeado mientras tanto |
| `tipologia` (por línea) | No existía | string enum | `Contenedor`, `CargaSuelta`, `GranelSolido`, `GranelLiquido`, `Alimentos`, `Refrigerados`, `CargaSeca` | Nuevo — distinto de `claseViaje` |
| `claseViaje` | No existía | string enum | `Contenedor`, `Cupo`, `CargaSuelta`, `Extradimensionado` | Nuevo, hardcodeado mientras tanto; define comportamiento condicional |
| `claseContenedor` | No existía | string enum | `CNT20.IT/RE/ST`, `CNT40.HC/RE` | Nuevo, hardcodeado, condicional a `claseViaje = "Contenedor"` |
| `exclusivo` / `consolidado` | No existía | boolean | — | Nuevo |
| `retornaContenedor` + `ciudadDevolucionContenedor` | No existía | boolean + municipio | — | Nuevo, condicional |
| `levanteContenedorDiferente` + `ciudadLevanteContenedor` | No existía | boolean + municipio | — | Nuevo, condicional |
| `valorAprovisionamiento` (por línea) | No existía | decimal | — | Nuevo |
| `peso` (por línea) | Ya existente | decimal | — | Sin cambios |
| `valorMercancia` (por línea) | No existía | decimal | — | Nuevo — distinto de `valorEstimado` (nivel encabezado) |
| `posiciones[]` (cubicaje) | No existía | array de objetos | `descripcion`, `cantidad`, `largoCm`, `anchoCm`, `altoCm`, `pesoKg` | Nuevo (HU-005.4) |
| Cliente (`clienteId` / `clienteBp`) | `cotizaciones.nitCliente` | string | Buscador Nexo (`GET /captura-solicitudes/clientes`) | Resuelto (HU-005.2) — la creación de solicitud requiere `clienteId`/`clienteBp`, no el NIT directo |
| Resultado (`ganada` / `perdida`) | `cotizaciones.ganada` / `motivo_rechazo` | boolean + texto | `Ganada`, `Perdida`, `SinDefinir` | Resuelto (HU-005.14) |
| Estado (`Pendiente` / `EnCosteo` / `Entregada` / `Descartada`) | `cotizaciones.estado_costeo` | string enum | 4 valores reales en Nexo frente a 3 diseñados en Cotifly (`pendiente_costeo` / `autocosteo` / `entregada`) | Por revisar — no corresponden exactamente; ajustar HU-005.7 |

### Decisiones confirmadas con negocio (2026-09-26)

- **`tipo` (spot / licitación):** aplica igual en Cotifly; se incorpora tal cual.
- **`modalidad`:** no se incorpora. La línea de negocio ya cubre esta necesidad en Cotifly; agregarla sería redundante.
- **Catálogos cerrados de Nexo** (SICE-TAC, carrocerías, clase de contenedor, clase de viaje): Cotifly reutiliza los mismos códigos de Nexo sin adaptarlos, mostrando siempre una etiqueta amigable en el frontend — nunca el código técnico crudo.
- **`origen` / `destino`:** se resuelven contra `GET /api/captura-solicitudes/municipios` mediante autocompletado simple (no cascada departamento → ciudad).
- **Catálogos sin endpoint propio en Nexo** (`tipoVehiculo`, `carroceria`, `claseViaje`, `claseContenedor`): se hardcodean en el frontend como deuda técnica documentada, a reemplazar por el consumo real en cuanto Nexo exponga cada endpoint, sin rediseñar el formulario.

### Pendiente de ajustar (no bloqueante para cerrar HU-005.1)

- **Estado (`estado_costeo`):** los 3 valores diseñados en Cotifly no corresponden exactamente a los 4 valores reales de Nexo. Requiere revisión y ajuste de HU-005.7 antes de implementar la derivación.

---

## Parte 2 — Actualización 2026-10-05: verificación contra código fuente

### Contexto

Duban solicitó el listado completo de campos del formulario "Nueva cotización" para construir el endpoint que recibe la solicitud estructurada completa, en reemplazo de la composición actual en lenguaje natural hacia `/web/chat`. Cada campo listado a continuación fue verificado contra el código fuente de los tres repositorios involucrados, sin asumir vigencia del mapeo original:

- **Nexo** (`NEXO`): contrato real de `POST /api/captura-solicitudes` (`CapturaRequests.cs`, `clsCapturarSolicitudCommand.cs`, `Enums.cs`).
- **`administracion_embarqa_nexo`** (frontend propio de Nexo, módulo `cabina-fletes`): captura práctica de los mismos campos.
- **Cotifly `backend-dotnet`**: estado actual de `/web/chat` y de los endpoints estructurados ya construidos.

### Hallazgo principal: existe un endpoint estructurado ya construido

`POST /web/solicitudes-costeo` (`clsCosteoController.cs`) ya recibe la práctica totalidad de este formulario en una sola llamada:

- DTO de entrada: `clsSolicitudCosteoWebRequest` + `clsPosicionCargaWebRequest` (`src/Cotifly.Api/Costeo/DTOs/clsDtosCosteo.cs`).
- Modelo de dominio: `clsSolicitudCosteo` / `clsPosicionCarga` (`src/Cotifly.Dominio/Cotizaciones/clsSolicitudCosteo.cs`), con validación incluida (`clsSolicitudCosteo.Validar()`, tolerancia del 1% entre el peso declarado de la línea y la suma de posiciones).
- Servicio: `clsEnvioCosteoService.cs` → `clsCosteoNexo.cs`, responsable de la comunicación con Nexo.
- Contrato documentado en `specs/openapi.yaml` (schemas `SolicitudCosteoWeb` / `PosicionCargaWeb`, líneas 2446-2588).

El endpoint acepta **una línea por llamada**, no un arreglo de líneas — consistente con el patrón ya implementado en el wizard (un turno de `/web/chat` por línea, de forma secuencial), por lo que no se requiere rediseñar esa parte del flujo.

Este endpoint no está documentado en `docs/docs_front/CONTRATO_API_WEB.md` (el contrato congelado para este frontend Angular); ese documento es anterior a su construcción (ADR-015) y debería actualizarse una vez se confirme el flujo de integración.

**Para contexto adicional:** el estado actual de `/web/chat` solo rastrea un subconjunto mínimo de campos en el estado de conversación (`LineaNegocioId`, `LineaNegocio`, `Origen`, `Destino`, `TipoVehiculo`, `TipoCarga`, `PesoKg` — `clsEstadoConversacion.cs:29-36`); el resto de los campos del formulario nunca se extraen de forma estructurada en ese flujo. Esto refuerza la necesidad de migrar a `/web/solicitudes-costeo`.

### Tabla de campos verificados (`cotizador.component.ts`, wizard de 6 pasos)

| Campo en el formulario | Nivel | Campo real en Nexo (`POST /api/captura-solicitudes`) | Campo en `SolicitudCosteoWeb` | Estado |
|---|---|---|---|---|
| `cliente` (texto, nombre) | Encabezado | No existe como campo de captura — Nexo acepta únicamente `clienteId` (GUID) o `clienteBp` (código BP de SAP); nunca el NIT directamente | `nit_cliente` | Sin gap en esta capa: `SolicitudCosteoWeb` ya acepta NIT y lo resuelve internamente |
| `idSap` (NIT) | Encabezado | Ver fila anterior | `nit_cliente` | Correcto |
| `tipo` | Encabezado | `tipo`: `"spot"` \| `"licitacion"` | `tipo` | Correcto |
| `deadline` | Encabezado | `deadline`: ISO 8601 | `deadline` | Cotifly envía una fecha simple (`AAAA-MM-DD`); confirmar que el backend la convierte a ISO 8601 antes de reenviarla a Nexo |
| `horasCargue` | Encabezado **y también por línea** | Existe a nivel de encabezado y de línea (el valor de línea sobreescribe al de encabezado) | `horas_cargue` | Gap: el formulario solo lo captura a nivel de encabezado |
| `horasDescargue` | Encabezado y también por línea | Igual que el anterior | `horas_descargue` | Mismo gap |
| `personalizacion` | Encabezado | `notas` | No forma parte de `SolicitudCosteoWeb` — es específico del mensaje/PDF, no del contrato de Nexo | Correcto, no aplica a este endpoint |
| (sin campo en el formulario) | Encabezado | `referencia`: referencia u orden de compra del cliente, distinta de `notas` | `referencia` | Gap: el formulario no tiene ningún campo para esto |
| (sin campo en el formulario) | Encabezado | `valorEstimado`: valor estimado de la solicitud, uno por encabezado — distinto de `valorMercancia`, que es por línea | `valor_estimado` | Gap: el formulario no tiene ningún campo para esto |
| `lineaNegocio` | Línea | Texto libre; Nexo lo resuelve por coincidencia aproximada contra el enum `PuertoAInterior` / `InteriorAPuerto` / `Nacional` / `DevolucionContenedor` / `Andino` / `Licitaciones`. El frontend propio de Nexo envía frases completas en lugar de los nombres del enum (p. ej. "Oferta de Expedición Nacional", "Oferta de Expedición Puerto a interior") | `linea_negocio` | El catálogo de Cotifly (`Nacional`, `Distribucion`, `Importacion`, `Exportacion`, `Internacional`, `Almacenamiento`, `Almacenamiento3PL`, `ITR`) no corresponde al catálogo real de Nexo. Confirmar que `MargenesPolitica.mapeoLineasNexo` traduce correctamente antes de reenviar |
| `origen` / `destino` | Línea | Texto libre, máximo 150 caracteres | `origen` / `destino` | Correcto, ya autocompletado contra municipios |
| `peso` | Línea | Requerido si las posiciones no aportan peso; si ambos vienen, deben coincidir dentro de un 1% de tolerancia | `peso_kg` | Correcto, mismo criterio ya validado en `clsSolicitudCosteo.Validar()` |
| `valorMercancia` | Línea | `valorMercancia` | `valor_mercancia` | Correcto |
| `tipoVehiculo` | Línea | Texto libre; coincidencia aproximada contra catálogo existente. **Si no encuentra coincidencia, Nexo crea una fila de catálogo nueva** en lugar de rechazar el valor — riesgo de duplicados si el texto enviado no es consistente | `tipo_vehiculo` | El catálogo que Cotifly consume hoy (`/web/catalogos-flete`) proviene de una fuente incorrecta — ver punto 3.1 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md` |
| `carroceria` | Línea | Texto libre, se resuelve en conjunto con `tipoVehiculo` | `carroceria` | Mismo origen de datos que el campo anterior |
| `claseViaje` | Línea | Texto libre; coincidencia aproximada contra el enum `Contenedor` / `Cupo` / `CargaSuelta` / `Extradimensionado` | `clase_viaje` | Correcto — "Carga Suelta" con espacio es el valor correcto, confirmado también en el frontend propio de Nexo |
| `claseContenedor` | Línea, condicional a `claseViaje = "Contenedor"` | Texto libre; coincidencia contra la lista `CNT20.IT` / `CNT20.RE` / `CNT20.ST` / `CNT40.HC` / `CNT40.RE`. Existe además una lista provisional adicional para el caso de extradimensionado nacional (`CNT20.FT`, `CNT20.OP`, `CNT40.FT`, `CNT40.OP`), marcada como provisional en el propio código de Nexo | `clase_contenedor` | Correcto, coincide exactamente con el catálogo vigente en Cotifly |
| `exclusivo` | Línea | Boolean | `exclusivo` | Correcto |
| `consolidado` | Línea | Boolean | `consolidado` | Correcto |
| `retornaContenedor` | Línea | Boolean. **Nexo lo fuerza a verdadero del lado del servidor** si `ciudadDevolucionContenedor` viene informado, independientemente del valor enviado en este campo | `retorna_contenedor` | Correcto — el valor enviado por el formulario es consistente, y la derivación del servidor actúa como respaldo |
| `ciudadDevolucion` | Línea, condicional | `ciudadDevolucionContenedor` | `ciudad_devolucion_contenedor` | Correcto (el nombre del control en el formulario difiere del nombre real del campo; mismo criterio de nombres distintos aplicado también en Nexo) |
| `levanteContenedorDiferente` | Línea | Boolean. **Nexo lo fuerza a verdadero del lado del servidor** si `ciudadLevanteContenedor` difiere del origen | `levante_contenedor_diferente` | Correcto, mismo criterio que el campo anterior |
| `ciudadLevante` | Línea, condicional | `ciudadLevanteContenedor` | `ciudad_levante_contenedor` | Correcto |
| (sin campo en el formulario) | Línea, condicional a `ciudadLevante` | `valorAprovisionamiento`: solo se persiste cuando la ciudad de levante difiere del origen | `valor_aprovisionamiento` | Gap: el formulario no tiene ningún campo para esto |
| `tipologiaCarga` (capturado por **mercancía/posición**) | — | `tipologia` existe únicamente a **nivel de línea**; no existe a nivel de posición en ningún punto del contrato de Nexo | `tipologia` (a nivel de línea en el DTO) | Discrepancia real: el wizard captura "Tipología" como campo de cada mercancía (incorporado 2026-10-02), sin equivalente en el contrato de Nexo a ese nivel. El frontend propio de Nexo no tiene ningún campo de tipología editable en la captura; es un valor que el backend infiere posteriormente. Requiere decisión: trasladar el campo a nivel de línea, o eliminarlo de la captura |
| `descripcion` / `cantidad` / `largoCm` / `anchoCm` / `altoCm` / `pesoKg` (por posición) | Posición | Mismos campos. `largoCm`/`anchoCm`/`altoCm` son obligatorios en el contrato de Nexo (0.1–999.999); `pesoKg` es opcional y se interpreta **por unidad**, multiplicado por `cantidad` para obtener el peso total de la posición | `descripcion` / `cantidad` / `largo_cm` / `ancho_cm` / `alto_cm` / `peso_kg` | Correcto, coincide exactamente; confirmado en dos fuentes independientes (Nexo y su propio frontend) |

### Servicios conexos: no forman parte de este formulario

A diferencia de los campos anteriores, los servicios conexos no forman parte de `SolicitudCosteoWeb` ni de `CapturaLineaRequest` — no existe ningún campo para esto en la captura de la solicitud, confirmado en los tres repositorios revisados. La razón es estructural: en Nexo, un servicio conexo se asocia a una línea de una **cotización ya existente** (`clsConexoItem.LineaId`, obligatorio desde la PR #257), y una cotización solo existe después de que la solicitud se costeó — es decir, en una etapa posterior a la que cubre este documento.

- **Catálogo de servicios disponibles:** `GET /api/servicios-conexos` (paginado; roles: todos).
- **Registro de la selección del comercial:** `POST /api/cotizaciones/{id}/conexos` (roles: Comercial, CoordinadorFletes, Admin; cuerpo `{ items: [{ servicioConexoId, cantidad?, valorAplicado? }] }`); requiere el identificador de una cotización ya costeada.
- **Estado actual en Cotifly:** `clsCosteoNexo.cs` únicamente **lee** los conexos que ya traiga la cotización al costearla (`LeerCotizacion`); no implementa ninguna llamada a `POST /api/cotizaciones/{id}/conexos` para registrar la selección del comercial. Documentado también en el punto 3.3 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`.
- **Relación con el wizard:** corresponde al Paso 4 ("Servicios conexos"), hoy un marcador de posición ("Próximamente"). No puede implementarse hasta contar con un identificador de cotización real — es decir, después de invocar `/web/solicitudes-costeo` por cada línea — y hasta que se implemente el método faltante en `clsCosteoNexo.cs`.

### Catálogos maestros de Nexo (`GET /api/catalogos/*`)

Nexo expone catálogos maestros que Cotifly todavía no consume como fuente — ver también los puntos 3.1 y 3.2 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`.

| Catálogo | Endpoint real de Nexo | Forma de la respuesta | Fuente que usa Cotifly hoy |
|---|---|---|---|
| Tipos de vehículo | `GET /api/catalogos/tipos-vehiculo` | `{ codigo, descripcion, esCatalogoOficial }` (códigos SAP cortos, p. ej. `"2S3"`) | `GET /api/biblioteca-sap-historico/filtros` — fuente incorrecta (histórico de tarifas, no catálogo maestro) |
| Clases de viaje | `GET /api/catalogos/clases-viaje` | `{ id, descripcion }` | Misma fuente incorrecta que el campo anterior |
| Clases de contenedor | `GET /api/catalogos/clases-contenedor` | `{ id, descripcion, tamanoContenedor, pesoInfCnt, pesoSupCnt }` | Hardcodeado en Cotifly; nunca consulta a Nexo |
| Líneas de negocio | `GET /api/catalogos/lineas-negocio` | `{ id, descripcion }` | Cotifly usa su propio catálogo (`MargenesPolitica`) con mapeo manual — comportamiento intencional, no un vacío (ver nota 3.4 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`) |
| Carrocería | No se encontró un endpoint equivalente en `/api/catalogos/*` | — | Hardcodeado; pendiente de confirmar con Duban si existe o si el campo permanece así |
| Tipología de carga | Sin catálogo propio — es un enum puro; Nexo lo resuelve por coincidencia de texto libre | — | Hardcodeado en Cotifly, coincide con los 7 valores reales del enum |

Estos catálogos existen para otros formularios de Nexo; `POST /api/captura-solicitudes` no valida contra ellos — acepta texto libre y lo resuelve por coincidencia aproximada del lado del servidor. Migrar a estos catálogos mejora la calidad de las opciones mostradas al comercial y reduce el riesgo de desincronización con Nexo, pero no es, por sí mismo, un bloqueante para que la captura funcione.

### Respuesta y manejo de errores de `POST /api/captura-solicitudes`

Relevante para el diseño de la integración, independientemente de que Cotifly use `/web/solicitudes-costeo` como intermediario:

- **Respuesta exitosa:** `201 Created`, cuerpo `clsCapturaResultDto`: `{ id, lineas, rutasCreadas, vehiculosCreados, scorePrioridad, fletesCalculados, lineasPorCostear, costeoEnProceso }`.
- **Códigos de error documentados:** `400 CLIENTE_REQUERIDO`, `400 CLIENTE_INVALIDO`, `400 CLIENTE_BP_INVALIDO`, `400 DWCUBOS_NO_CONFIGURADO`, `400 ENUM_INVALIDO`, `400 LINEA_INCOMPLETA`, `400 PESO_REQUERIDO`, `400 PESO_INCONSISTENTE`, además de los genéricos `401` / `403` / `400 VALIDATION_ERROR` / `500`.

### Resumen de pendientes para completar la integración

1. **Agregar al formulario:** `referencia` (encabezado), `valorEstimado` (encabezado) y `valorAprovisionamiento` (por línea, condicional a ciudad de levante).
2. **Permitir `horasCargue` / `horasDescargue` también a nivel de línea** (hoy solo existen en el encabezado); no bloqueante, ya que el encabezado funciona como valor por defecto si la línea no lo sobreescribe.
3. **Decidir el tratamiento de "Tipología" por mercancía** — no tiene equivalente real en Nexo a ese nivel; evaluar si se traslada a nivel de línea o se retira de la captura.
4. **Confirmar con Duban** que `clsCosteoNexo.cs` resuelve correctamente la identificación del cliente por NIT, el mapeo de `lineaNegocio`, y que los catálogos de `tipoVehiculo` / `carroceria` / `claseViaje` / `claseContenedor` usan las fuentes correctas (ver sección 3 de `docs/backlog/2026-09-29_PENDIENTES_CORRECCION_DUBAN.md`).
5. **Actualizar `docs/docs_front/CONTRATO_API_WEB.md`** para documentar `POST /web/solicitudes-costeo` como el endpoint vigente para este frontend, en reemplazo de la referencia actual a `/web/chat`.
6. **Servicios conexos:** implementar la llamada faltante a `POST /api/cotizaciones/{id}/conexos` en `clsCosteoNexo.cs`, y definir en qué punto del flujo de Cotifly se le presenta al comercial la opción de elegir cuáles incluir — necesariamente después del costeo, no durante la captura inicial.
7. **Migrar los catálogos de tipo de vehículo y clase de viaje** a `GET /api/catalogos/*` en lugar de `biblioteca-sap-historico/filtros`; confirmar con Duban si existe un catálogo real para carrocería.

---

## Parte 3 — Actualización 2026-10-13: envío por camión con los datos del cubicador (HU-023.4, #159)

Verificado contra la rama `QA` de Nexo (fusionada, **todavía no desplegada**): `CapturaRequests.cs`, `clsLineaCosteoDto`,
`clsCapturarSolicitudValidador.cs` y `clsIdentidadPortalAuthenticationHandler.cs`. Decisiones del usuario del 2026-10-13; detalle en
`backend-dotnet/specs/decisiones/ADR-020-conexion-con-el-cubicador.md`.

| Dato de Cotifly | Campo de Nexo | Nivel | Regla en Cotifly |
|---|---|---|---|
| `UsuariosWeb.idUsuarioExterno` del comercial | Encabezado HTTP **`X-Portal-clsUsuario-Id`** | Toda petición | Siempre. Nexo responde 401 al nombre anterior (`X-Portal-Usuario-Id`). Un solo lugar: `clsIdentidadNexo` |
| Id de la ruta (`CotizacionLineasSolicitud.id`) | `grupoRutaId` (`Guid?`), también en `clsLineaCosteoDto` del GET | Línea | Con `Nexo:EnviarGrupoRutaId` (encendida por defecto desde #176). Solo en las líneas de una ruta con **varios** camiones (contiguas); una ruta de un solo camión ("línea suelta") va con `null` (contrato §3.3). Antes `referenciaRuta` (N1) |
| `CotizacionVehiculosLineaSolicitud.idRecomendacionVehiculo` | `idRecomendacionVehiculo` (texto, máx. 64), también en el GET | Línea | Con `Nexo:EnviarDatosCubicador` (encendida por defecto desde #176). GUID en texto (36); el cubicador lo da en MAYÚSCULAS y se compara como GUID |
| `CotizacionMercancias.codigoMercancia` | — | — | **No viaja** (contrato #297): Cotifly lo guarda solo para el cubicador |
| Orden de envío (rutas por `orden`, camiones por `OrdenCreacion`) | `numeroLinea` (solo en el GET, desde 1) | Línea | Sin campo de entrada: Nexo numera por el orden del arreglo. Desempate fuerte de camiones idénticos de un mismo `grupoRutaId` |
| `tipoVehiculo` / `carroceria` del camión del cubicador (`2S2`, `FURGON`) | `tipoVehiculo` / `carroceria` | Línea | Con `Nexo:EnviarCodigosCubicador` (encendida por defecto desde #176: Nexo ya desplegó la cuarentena, 18.4, y un código desconocido nace en cuarentena). En `false` (emergencia), la línea va sin vehículo y la resuelve la Mesa |
| `origenMunicipioId` / `destinoMunicipioId` | `origenMunicipioId` / `destinoMunicipioId` | Línea | HU-023 (#176): el id sale de la búsqueda `GET /api/captura-solicitudes/ciudades` (`GET /web/municipios?q=`); es el mismo id de `tbl_municipios` que el catálogo completo de `/municipios` |
| `tipoVehiculo` / `carroceria` del respaldo (`Captura:AceptarVehiculoCliente`) | `tipoVehiculo` / `carroceria` | Línea | Siempre: es un código del catálogo de Nexo |
| Peso | `peso` | Línea | Un solo camión: el **peso declarado de la ruta** (con las posiciones; cuadra al 1 % y evita `PESO_INCONSISTENTE`). Varios camiones: el `pesoTotalKg` de cada camión, sin posiciones. El peso del cubicador se guarda y se muestra en Cotifly |
| Camión sin vehículo | `tipoVehiculo`/`carroceria` vacíos | Línea | Sin cambios; se espera la 19.2 de Nexo (`PorCostear`) |

**Emparejamiento en el polling:** con `idRecomendacionVehiculo` en el GET, por ese campo y por `grupoRutaId` si viene (la línea suelta
no lo trae, HU-023 #176; el camión del cubicador es único igual); solo con
`grupoRutaId`, por grupo, luego por contenido y, entre camiones idénticos, por `numeroLinea`; sin ellos, por contenido y orden estable
(como antes). El payload acordado completo está en ADR-020 ("Ajuste al contrato acordado con Nexo").

**Cupo de lecturas:** Nexo limita sus listados **por rol** (`listados-estandar` 120/min; `listados-pesados` 20/min, 60 para Admin) y
responde `429 DEMASIADAS_SOLICITUDES` con `Retry-After: 30`. El cupo lo comparten todos los comerciales de Cotifly. Cotifly no reintenta
el 429; solo `/api/catalogos/*` y la bandeja `GET /api/solicitudes` (el listado) se bloquean hasta el `Retry-After` (fecha pasada = 0 s; sin encabezado, 30 s; tope 5 min); el polling (`/api/solicitudes/{id}`), las cotizaciones, los conexos, los clientes y los municipios no se congelan, y un 429 de catálogos no aumenta el backoff del polling. El error es "no disponible" (`COSTEO_NEXO_NO_DISPONIBLE`), no "rechazada".
