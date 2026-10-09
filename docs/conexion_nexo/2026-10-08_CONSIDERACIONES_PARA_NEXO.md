# Consideraciones para Nexo y el cubicador — reestructuración de cotizaciones de Cotifly

**Fecha:** 2026-10-08. **Origen:** HU-021 (`docs/backlog/2026-10-08_HU_Correccion_BD_Cotifly.md`).
**Para quién:** el equipo de Nexo y quien construye el cubicador (que vive en `NEXO/cubicador`).
**Qué es este documento:** lo que Cotifly le **pide o le avisa** a Nexo y al cubicador. **No son issues de Cotifly**: es código de otros sistemas, que Cotifly no puede hacer por su cuenta. Cada petición dice qué pasa si no se hace.

---

## 1. Qué cambia en Cotifly, en una frase

Cotifly pasa de "una cotización = una ruta = un vehículo" a **solicitud → rutas → camiones**. El comercial ya no digita el vehículo ni la carrocería: los recomienda el **cubicador**, que puede proponer **varios camiones para una misma ruta**. Cada camión se cuesta aparte en Nexo.

Diseño completo: `docs/database/PROPUESTA_REESTRUCTURACION_COTIZACIONES.md`.

## 2. Lo que va a cambiar en lo que Cotifly le manda a Nexo

Nexo no tiene que hacer nada para recibir esto: sigue siendo `POST /api/captura-solicitudes`. Es para que lo sepan.

- **Una línea por camión.** Si el cubicador recomienda 3 camiones para una ruta, Nexo recibe 3 líneas con el mismo origen, destino, tipología, clase de viaje y condiciones de contenedor, y distinto vehículo y carrocería.
- **Sin `posiciones[]`.** Cada línea lleva solo el peso del camión. Nexo acepta peso total o bultos con peso (`CapturarSolicitudValidador.ValidarLinea`), no exige los bultos.
- **Una línea puede llegar sin vehículo ni carrocería** cuando el cubicador no pudo recomendar. Hoy Nexo ya lo acepta (no hay validación que lo exija).
- **Origen y destino** se siguen mandando como el nombre del municipio, que Cotifly resuelve a partir del id con `GET /api/captura-solicitudes/municipios`.
- **El encabezado** ya no lleva `valorEstimado` ni `referencia`. `horasCargue` y `horasDescargue` van **por línea** (cada camión lleva las de su ruta), como Nexo ya las acepta desde HU-013.11.
- **El header** sigue siendo `X-Portal-clsUsuario-Id`: Cotifly lo mantiene permanente (decisión del 2026-10-05).
- **Los servicios conexos** de una ruta se enviarán pegados a la línea del **primer camión** de esa ruta, porque Nexo guarda los conexos por línea.

## 3. Peticiones a Nexo

| # | Petición | Prioridad | Si no se hace |
|---|---|---|---|
| N1 | Campo en la línea que identifique la ruta | Alta | Cotifly empareja por orden de envío: dos camiones iguales pueden mostrar el estado cruzado |
| N2 | ~~Recibir las horas de cargue y descargue por línea~~ | **Ya resuelta** | — |
| N3 | Poder asignar vehículo y carrocería a una línea que llega vacía | Media | El admin de fletes solo puede ponerle el precio |
| N4 | Confirmar con una prueba real que una línea sin vehículo entra a la Mesa | Baja | Se asume lo que dice el código |

### N1. Campo de ruta en la línea

Cotifly le enviará una línea por camión, y los camiones de una misma ruta comparten todo menos el vehículo. Al volver el costeo (`GET /api/solicitudes/{id}`), Cotifly tiene que saber **qué línea de Nexo corresponde a qué camión suyo**. Hoy lo hace por contenido (origen, destino, vehículo, carrocería, peso), y si dos camiones son iguales solo los distingue el orden.

**Petición:** aceptar en `CapturaLineaRequest` un campo **opcional** que identifique la ruta, `referenciaRuta` (texto de hasta 64 caracteres; Cotifly manda el GUID de su ruta), guardarlo en `tbl_lineas_solicitud` y devolverlo junto a cada línea en `GET /api/solicitudes/{id}`. No se propone `rutaId` porque `LineaCosteoDto.rutaId` ya existe en Nexo (es su catálogo de rutas origen-destino).

**Estado en Cotifly (HU-021.7, #134):** listo del lado de Cotifly y apagado (`Nexo:EnviarReferenciaRuta = false`) hasta que Nexo lo acepte, porque su `CapturaLineaRequest` declara `additionalProperties: false` y no tiene ningún campo de texto libre por línea donde ponerlo. Sin N1, Cotifly empareja por contenido, asigna en orden estable los camiones idénticos de una misma ruta y pone en `notas` del encabezado cuántas líneas son de cada ruta, para la Mesa.

**Para qué le sirve a Nexo:** la Mesa de Costeo podría mostrar agrupadas las líneas de una misma ruta. Cotifly lo usa solo para emparejar.

**Costo estimado en Nexo:** 1 a 2 días (es un campo opcional que se guarda y se devuelve).

### N2. Horas de cargue y descargue por línea

**Ya resuelta, no hay que pedir nada.** Se había supuesto que el contrato pedía `horasCargue`/`horasDescargue` una sola vez, en el encabezado de `CapturaSolicitudRequest`. Pero Nexo **ya las acepta en cada línea** de `CapturaLineaRequest` (sobrescriben su valor por defecto), y `QA` ya las guarda por línea (migración `0008_horas_por_linea`, HU-013.11, #108). Cotifly manda a cada camión las horas de su ruta.

### N3. Vehículo y carrocería de una línea que llega vacía

Cuando el cubicador responde "sin selección automática" (o la mercancía está condicionada y pendiente de aprobación), la línea llega a Nexo **sin vehículo** y el admin de fletes la resuelve en la Mesa de Costeo.

Hoy, en la Mesa, **el vehículo solo se muestra**, no se puede cambiar:
- La página solo tiene chips de "Tipología del vehículo" y "Carrocería" (`mesa-costeo-page.component.html:120-121`) y el formulario de flete solo tiene `valorFlete`, `fuente` y `valorRetornoContenedor` (`mesa-costeo-page.component.ts:905-909`).
- Ningún comando actualiza `ConfigVehiculoId` después de la captura; solo se asigna al crear (`clsCapturarSolicitudCommand.cs:123`, `clsCrearSolicitudCommand.cs:49`, `clsCargarLineasBulkCommand.cs:45`).

**Petición:** un comando que asigne el vehículo y la carrocería de una línea, y que la Mesa lo ofrezca. **Esta parte toca el frontend de Nexo** (`administracion_embarqa_nexo`).

**Alternativa sin cambios en Nexo:** que el admin de fletes solo ponga el precio (como hoy) y el camión quede sin vehículo en Cotifly. Cotifly lo soporta; se pierde saber qué vehículo se usó.

### N4. Línea sin vehículo

Según el código (`clsCapturarSolicitudValidador.cs:35-52`, `clsResolucionDeCatalogos.ResolverVehiculo`, `clsPropuestaTarifaAdapter.cs:422-428`), una línea sin `tipoVehiculo` se acepta y se puede costear con una tarifa menos precisa (SICE-TAC sin filtro de vehículo). **Confirmarlo con una prueba real** antes de depender de ello.

## 4. Peticiones al cubicador

El cubicador hoy solo tiene el diseño (`PLAN-IMPLEMENTACION-MOTOR-SELECCION-VEHICULOS.md`), sin código de `POST /recomendaciones`.

| # | Petición | Prioridad | Si no se hace |
|---|---|---|---|
| C1 | `POST /recomendaciones`, `GET /tipos-mercancia` y `GET /tipos-carroceria` funcionando | Bloqueante | Cotifly no puede pedir camiones; sigue con "una ruta = un camión" y el vehículo digitado |
| C2 | Devolver el **peso total de cada vehículo** | Alta | Cotifly no tiene qué mandarle a Nexo como peso del camión |
| C3 | No sobrescribir recomendaciones | Alta | Una recomendación que Cotifly ya usó puede cambiar sin avisar |
| C4 | Mismos códigos de vehículo y carrocería que Nexo | Alta | Nexo puede crear filas de catálogo de vehículo que no existían, o rechazar el vehículo |
| C5 | Autenticación `Bearer` | Media | — |
| C6 | Devolver "sin selección" y "condicionada" como resultado, no como error | Media | Cotifly tomaría un caso normal como una falla |

### C1. Endpoints

Con `OrigenSistema = COTIFLY` e `IdLineaSolicitud` = el id de la ruta de Cotifly (un GUID, en texto). Por mercancía, Cotifly manda `idTipoMercancia` (del catálogo del cubicador), medidas, cantidad y peso, y recibe de vuelta un registro por vehículo recomendado.

### C2. Peso por vehículo

La Tabla 7 (`recomendacionVehiculo`) trae el % de llenado pero **no el peso del vehículo**; el peso solo está por mercancía en la Tabla 8. Cotifly necesita el **peso de cada camión**, porque es el que le manda a Nexo (la validación de Nexo compara el peso de la línea). **Petición:** devolverlo en la respuesta (la suma de sus mercancías) o agregarlo a la Tabla 7.

### C3. No sobrescribir recomendaciones

Cotifly guarda el **id exacto** de la fila de la Tabla 7 que usó (`idRecomendacionVehiculo`) y no la consulta por línea. Si el cubicador recalcula una ruta, que cree filas **nuevas** en las Tablas 7 y 8 y no modifique las anteriores.

### C4. Códigos de vehículo y carrocería

En Nexo el vehículo es **una sola combinación** del catálogo (tipo + carrocería + rango de peso, `tbl_configuraciones_vehiculo`), y las combinaciones desconocidas se agregan solas al catálogo (`clsResolucionDeCatalogos`). El cubicador maneja tipo (`3S3`, `2S2`…) y carrocería por separado. **Petición:** que usen los mismos códigos, o una sola tabla de equivalencias del lado del cubicador, que consuman los dos sistemas. Cada sistema cubica sus propias solicitudes: lo que nace en Cotifly se cubica en Cotifly, y Nexo no vuelve a cubicarlo.

### C5. Autenticación

`Authorization: Bearer <CUBICADOR_API_KEY>`, el mismo esquema de los endpoints que ya existen (confirmado por la sesión de NEXO el 2026-10-08). **Reconfirmar para `/recomendaciones` cuando exista.**

### C6. Resultados que no son error

- `SIN_SELECCION_AUTOMATICA`: que se devuelva con su motivo.
- Mercancía condicionada pendiente de aprobación de AdminTarifas: que se devuelva marcada, sin error. Cotifly envía entonces la solicitud **completa** a la Mesa de Costeo, sin partirla ni bloquearla.

## 5. Qué hace Cotifly mientras tanto

Sin nada de lo anterior, Cotifly funciona así:

- **Sin el cubicador (C1):** una ruta = un camión, con el vehículo digitado como hoy.
- **Sin N1:** empareja los camiones de una ruta por contenido y, los idénticos, en orden estable (HU-021.7, #134). Los costos no se cruzan (camiones iguales cuestan lo mismo), pero el estado de cada uno sí podría quedar en el camión equivocado. Dos rutas con el mismo contenido no se emparejan por orden: quedan sin asociar y sin precio hasta revisarlas con Nexo.
- **Sin N3:** el admin de fletes pone el precio de una línea sin vehículo.

## 6. Preguntas abiertas para Nexo

1. ¿Quién del equipo de Nexo toma N1, N3 y N4? (N2 ya está resuelta.)
2. ¿Se acepta el campo de ruta (N1) con ese formato, o prefieren otro nombre o tipo?
3. ¿La asignación de vehículo en la Mesa (N3) se hace, o basta con el precio?
4. ¿Existe un catálogo maestro de **carrocería** en `/api/catalogos/*`? No se encontró uno (hoy se usa `biblioteca-sap-historico/filtros`).

## 7. Contexto verificado en el código de Nexo (2026-10-08)

Para evitar suposiciones:

- La Mesa de Costeo guarda **flete, retorno de contenedor y fuente** por línea (`clsAgregarLineaCotizacionCommand.cs:62-70`). El valor de **levante o aprovisionamiento** no lo escribe el admin: se copia de lo que llegó en la captura (`clsLineaSolicitud.ValorAprovisionamiento`). En Cotifly, el comercial lo digita una vez por ruta y se copia a cada camión.
- Los **conexos** se guardan siempre **por línea** (`clsCotizacionConexo.CotizacionLineaId` es obligatorio; el conexo "general" a nivel de cotización se quitó a propósito, `clsCotizacion.cs:97-105`). La etiqueta "General / En N de M líneas / Individual" es un cálculo de la pantalla (`servicios-conexos.util.ts:150-162`), no un dato guardado.
- **Autocosteo:** si la Biblioteca devuelve un valor propuesto para una línea, se costea sola; si no, queda `PorCostear` y va a la Mesa (`clsCapturarSolicitudCommand.AutoCostearAsync`, `clsReglaDeCosteo.cs:27`).
- **No existe "cantidad de vehículos" por línea** en Nexo. Lo más parecido es el texto "Flota sugerida: N × unidad" de la pantalla, que no se guarda. Por eso Cotifly manda **una línea por camión**.
