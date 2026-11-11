# Diccionario de datos — `dbo_cotifly` (SQL Server)

> **Estado:** vigente desde HU-021 · **Dueño:** Duban Guerra (backend) y scalderon-bs (datos) · **Actualizado:** 2026-11-10 (HU-024.2, #203: todas las columnas del esquema consolidado `db/esquema/cotifly_esquema.sql`, con los ajustes `02` a `22`)

Qué significa cada columna de las tablas de cotizaciones, quién la escribe y quién la lee. Los tipos, nulos, restricciones e
índices están en [`ESQUEMA_OBJETIVO_SQLSERVER.md`](ESQUEMA_OBJETIVO_SQLSERVER.md) (Parte A, con el diagrama ER); la decisión
de diseño, en [ADR-019](../../backend-dotnet/specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md). Fuente de verdad:
`db/esquema/cotifly_esquema.sql` (el esquema completo: `0001` a `0010` más los ajustes `db/scripts/02` a `22`) y el modelo EF
(`backend-dotnet/src/Cotifly.Infraestructura/Comun/Persistence/Configurations/`). Entre paréntesis, el ajuste de
`db/scripts/` que trajo la columna a las bases que ya existían.

## 1. Para qué sirve esta base

Es la base de **trazabilidad** de Cotifly, no el ERP. Guarda la cotización completa: quién la pidió, qué rutas y camiones
tiene, qué costó cada camión en Nexo, con qué margen se le puso precio a cada ruta, qué PDF y qué correo salieron, y si el
negocio se ganó. Nexo (costos y conexos), SAP TM (margen) y el cubicador (vehículos) son fuentes externas: de ellas queda
solo la foto de lo que se usó.

## 2. Cómo se llena una cotización

1. **Captura** (`POST /web/solicitudes-costeo` o licitación por Excel): se crea la cabecera (`CotizacionesSolicitud`) con
   una fila por ruta (`CotizacionLineasSolicitud`), sus mercancías y sus conexos. El cubicador recomienda los camiones de
   cada ruta y se crea una fila por camión (`CotizacionVehiculosLineaSolicitud`). Si la ruta no se puede cubicar (solo peso o
   mercancías sin medidas), si no hay cubicador o si el cubicador no selecciona vehículo o no responde, la ruta queda con un
   camión con el vehículo elegido por el comercial (`tipoVehiculoElegido`/`carroceriaElegida`, HU-032, ADR-022) o, si no eligió
   ninguno, sin vehículo (lo resuelve la Mesa de Costeo de Nexo). Cubica el sistema donde nace la solicitud: si nace en
   Cotifly, Cotifly llama al cubicador; Nexo no vuelve a cubicar lo que le llega de Cotifly.
2. **Envío a Nexo:** una línea de Nexo por camión. La cabecera guarda `solicitudNexoId`.
3. **Polling:** empareja cada camión con su línea de Nexo (`lineaSolicitudNexoId`), guarda su flete, retorno y estado, y
   la ruta recalcula sus totales y su precio. Los conexos se envían a Nexo una sola vez, pegados al primer camión vigente
   de la ruta (decisión 10); el registro de cada envío va en `EventosHerramienta`.
4. **Ajustes del comercial** (margen por ruta, conexos, levante, textos): suben `versionNegocio`, recalculan la ruta y la
   cabecera y siguen la regla del PDF (ADR-018). El ajuste de margen queda en `MargenAjustes` con su ruta.
5. **PDF y correo:** `pdfPath`, `CotizacionPdfsHistorial`, `Correos` y `CorreoDestinatarios`. El correo sale por el
   microservicio MailSender (HU-033, ADR-023): Cotifly guarda el id que devuelve (`idMailSender`) y un worker consulta su estado
   (`encolado` → `enviando` → `enviado` o `fallido`) según la agenda `siguienteConsultaAt`.
6. **Resultado:** `ganada`, `motivoRechazo`, `marcadoAt`. Si el comercial lo marca antes de que Nexo entregue la cotización, queda
   solo en Cotifly (`sincronizacionResultadoNexo = pendiente`) y el polling del costeo lo envía a Nexo una sola vez cuando la Mesa
   la entregue (resultado diferido, HU-033).

## 3. `CotizacionesSolicitud` — la cabecera

Una fila por cotización (y por cada versión `-vN`). Antes del `0009` se llamaba `Cotizaciones` y tenía también los datos de
la línea; ahora esos datos son de la ruta.

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `id` | Id interno | Captura | Todo el agregado |
| `folio` | Identificador de negocio, único: `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]` (ADR-011, decisión 11) | Captura, con el candado `sp_getapplock` | API, PDF, correo, asistente |
| `clienteNombre`, `nitCliente`, `origenCliente` | Cliente (o prospecto) | Captura (búsqueda de clientes en Nexo) | PDF, Nexo, folio |
| `tipoSolicitud` | `spot` (formulario) o `licitacion` (Excel) | Captura | Envío a Nexo, PDF |
| `deadline` | Fecha límite que pide el cliente | Captura | Nexo, métricas |
| `notas` | Nota para la Mesa de Nexo (no sale en el PDF) | Captura | Nexo (al enviar, Cotifly le suma en el mensaje cuántas líneas son de cada ruta) |
| `moneda`, `trmAplicada` | Moneda de la cotización y TRM usada (0 en COP). Es de la cabecera: todas las rutas usan la misma (ADR-014) | Captura, re-solicitud, `-vN` | Cálculo del precio de cada ruta, PDF |
| `precioFinal` | Σ `precioFinalLinea` de las rutas, con conexos. NULL mientras alguna ruta no tenga precio | Dominio (recalculo) | "Mis cotizaciones", PDF, métricas |
| `estadoCosteo` | Resumen del estado de las rutas ("el más atrasado") | Dominio | API, polling, métricas |
| `motivoEstadoCosteo` | Explicación del estado (por ejemplo `SOLICITUD_NEXO_DESCONOCIDA`) | Envío, polling | API |
| `solicitudNexoId` | Id de la **solicitud** de Nexo (lo devuelve el alta; lo consulta el polling). Antes `costeoId` (0010) | Envío a Nexo | Polling, resultado |
| `scorePrioridad` | `scorePrioridad` que Nexo devuelve al crear la solicitud (`decimal(9,4)`, NULL si no lo mandó o se envió antes del script `09`; se limpia al re-solicitar). HU-018.1 (#155) | Envío a Nexo | "Ver detalle" (`score_prioridad`) |
| `cotizacionNexoId` | Id de la **cotización** de Nexo | Polling | Conexos, resultado en Nexo |
| `costeoEnviadoAt`, `costeoSiguienteConsultaAt`, `costeoConsultasFallidas`, `costeoListoAt` | Agenda y seguimiento del polling | Envío, polling | Worker del polling |
| `versionNegocio` | Versión de negocio: viaja como `ETag` y vuelve en `If-Match` (428/409) | Cada cambio que ve el comercial y el polling cuando cambia algo | Concurrencia optimista (ADR-018) |
| `version` | Número de versión del folio (`-vN`) | Nueva versión | Folio, asistente |
| `pdfPath`, `pdfDesactualizado` | PDF vigente y si un cambio lo invalidó | Generación del PDF; mutaciones | PDF, correo |
| `textosCotizacion` | JSON con asunto e introducción del PDF (IA con respaldo estándar) | Redacción (ADR-018) | PDF, correo |
| `estadoEnvio` | `sin_enviar`, `enviada`, `reenviada` | Correo | Regla del PDF, métricas |
| `ganada`, `motivoRechazo`, `marcadoAt` | Resultado del negocio | Seguimiento | Métricas |
| `finalizadaAt`, `finalizadaPor` | Cuándo y quién (`UsuariosWeb.id`, FK `fk_cotizaciones_finalizadaPor`) finalizó la cotización: el comercial aceptó el precio y desde ahí queda congelado (margen, conexos, re-solicitud y edición piden una versión nueva). Las dos NULL (sin finalizar) o las dos con valor (CHECK `ck_cotizaciones_finalizada`). Van aparte de `estadoCosteo`, que el polling recalcula desde Nexo (script `14`, HU-036 #205) | `POST /web/cotizaciones/{folio}/finalizar` | Candados de edición, PDF, correo, resultado |
| `sincronizacionResultadoNexo` | Estado del envío del resultado a Nexo: `pendiente` (marcado antes de la entrega en Nexo), `sincronizado` o `no_sincronizable`. NULL = sin resultado o anterior al script `13` (HU-033) | Resultado, polling del costeo (`clsSincronizacionResultadoNexoService`) | API, polling |
| `comentarioResultadoPendiente` | Comentario que se enviará a Nexo con el resultado pendiente (hasta 500, límite de Nexo; `13`) | Resultado | Polling (envío diferido) |
| `resultadoSincronizadoAt`, `errorSincronizacionResultado`, `intentosSincronizacionResultado` | Cuándo Nexo aceptó el resultado, el último error (o por qué ya no se puede enviar) y los intentos fallidos (`13`) | Polling del costeo | API, polling (reintentos hasta `CosteoNexo:MaximoFallosSincronizacionResultado`) |
| `usuarioId` | Comercial dueño | Captura | Autorización, métricas |
| `spaceId`, `threadId` | Hilo de origen (legado de Google Chat, retirado en #105) | Solo la cotización legada del chat (`clsGenerarPdfService`, apagada por defecto con `Pdf:PermitirCotizacionChat`) | Lectura de cotizaciones antiguas |
| `createdAt`, `updatedAt` | Marcas de tiempo | Interceptor de EF | Listados |

**Retiradas por el `0009` o la decisión 1:** `referencia`, `valorEstimado`, `costeoId` (vuelve como `solicitudNexoId`),
`costeoUltimaConsultaAt`, los campos de la línea (origen, destino, vehículo, costo, margen, línea de negocio) y el JSON
`serviciosConexos`.

## 4. `CotizacionLineasSolicitud` — la ruta

Una fila por ruta que pidió el comercial (una fila del Excel en una licitación). Tiene lo que es igual para todos sus
camiones, los totales de costo, el margen y el precio.

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `id` | Id interno de la ruta | Captura | Camiones, mercancías, conexos, `MargenAjustes` |
| `cotizacionSolicitudId`, `orden` | Cabecera y posición de la ruta (1..N, única por cabecera) | Captura | API (`linea_orden`), PDF |
| `rutaOrigenIndice` | Posición en el formulario o fila del Excel (base 0) | Captura | Reporte de errores de la licitación, Excel exportado |
| `lineaNegocioId` | Línea de negocio de la ruta (`MargenesPolitica`). Cada ruta puede tener la suya | Captura | Margen de política, `lineaNegocio` de cada camión en Nexo, métricas por ruta |
| `origenMunicipioId`, `destinoMunicipioId` | Id del municipio en Nexo (`tbl_municipios`), obligatorio en toda captura nueva (decisión 2) | Captura (catálogo `GET /web/municipios`) | Envío a Nexo |
| `origenNombre`, `destinoNombre` | Copia del nombre del catálogo, para mostrar y para que la cotización no cambie si cambia el catálogo (0010) | Captura | PDF, emparejamiento, asistente |
| `ciudadDevolucionMunicipioId`, `ciudadDevolucionNombre`, `ciudadLevanteMunicipioId`, `ciudadLevanteNombre` | Ciudades del contenedor, id + nombre (decisión 2) | Captura | Nexo, PDF |
| `tipologia`, `claseViaje`, `claseContenedor`, `exclusivo`, `consolidado`, `retornaContenedor`, `levanteContenedorDiferente` | Condiciones del viaje y del contenedor (catálogos de Nexo) | Captura | Envío a Nexo |
| `horasCargue`, `horasDescargue` | Horas de la ruta (0 a 240) | Captura | Nexo |
| `pesoKg` | Peso total declarado de la ruta | Captura | Cubicador, respaldo de un camión |
| `pesoConTaraKg` | Peso total de la ruta CON la tara de los contenedores (`decimal(12,2)`, NULL = sin tara; solo en una ruta `Contenedor`, nunca menor que la carga; script `11`, HU-030.1 #184) | Captura, edición | Nexo: peso de las líneas de la ruta (la tara repartida por partes iguales entre los camiones; #250: con un camión la línea lleva sus posiciones, con varios no). No va al cubicador |
| `valorMercancia` | Valor de la mercancía (para el seguro; no es costo) | Captura | Nexo |
| `valorLevanteContenedor` | Un levante; lo digita el comercial y se copia a cada camión (decisión 4) | Captura, `CambiarValorLevante` | Camiones |
| `costoFletesTotal`, `costoRetornoContenedorTotal`, `costoLevanteContenedorTotal` | Suma de los camiones que cuentan | Dominio (polling) | Precio |
| `costoServiciosConexos` | Σ precio × cantidad de los conexos | Dominio | Precio |
| `costoFinal` | Fletes + retorno + levante + conexos | Dominio | Precio, métricas |
| `margenPoliticaSugerido`, `margenSAPSugerido` | Margen sugerido por la política de la línea y por SAP TM (fracción). El vigente es el de SAP si existe | Envío (resolución del margen) | Ajuste de margen, asistente |
| `margenAplicado`, `origenMargen`, `metodoCalculo` | Margen con el que se calcula el precio, de dónde salió (`sap_tm`, `politica_linea`, `negociado`) y el método (`margen` o `markup`) | Envío; ajuste por ruta (decisiones 6 y 9) | Precio, métricas |
| `precioFinalLinea` | `costoFinal` con el margen, en la moneda de la cabecera | Dominio | PDF, cabecera, métricas |
| `incluyeSeguro` | Casilla de la ruta: se cobra el seguro de la mercancía (script `20`, HU-045.1 #263). `0` en las rutas anteriores al `20` | Seguro de la ruta (`PUT /web/cotizaciones/{folio}/costeo/seguro`, HU-045.2 #264) | Precio |
| `tasaSeguroAplicada` | Copia de la tasa activa de `TarifasSeguro` con la que se calculó el seguro (fracción: 0.003450 = 0,345 %). Un cambio de tasa no mueve las cotizaciones hechas | Seguro de la ruta (HU-045.2) | Precio, PDF |
| `costoSeguro` | `valorMercancia` × `tasaSeguroAplicada`. Va aparte: no entra en `costoFinal` ni lleva margen. NULL si la ruta no lleva seguro o no tiene `valorMercancia` | Seguro de la ruta (HU-045.2) | Precio, PDF |
| `estadoCosteo` | Resumen del estado de sus camiones (8 estados; `rechazada` si lo están todos sus camiones vigentes, script `10`) | Dominio | Cabecera, API |
| `createdAt`, `updatedAt` | Marcas de tiempo; `createdAt` también ordena la cola de la cubicación | Interceptor de EF | Listados, worker de cubicación |
| `tipoVehiculoElegido`, `carroceriaElegida` | Vehículo que eligió el comercial en la captura: respaldo del cubicador (script `12`, HU-032, ADR-022). Se conserva en la edición y la re-solicitud aunque el cubicador o la Mesa cambien el del camión. NULL = no eligió | Captura, edición | Planificador de camiones (respaldo), API |
| `idRecomendacion`, `resultadoCubicacion`, `motivoSinSeleccion`, `condicionada` | Resultado de la cubicación de la ruta (script `02`): la corrida del cubicador, `ASIGNADO` o `SIN_SELECCION_AUTOMATICA`, el motivo (5 valores, con `BUSQUEDA_AGOTADA` del `07`) y si lleva mercancías condicionadas | Captura, recálculo | API, Nexo |
| `estadoCubicacion`, `cubicacionIntentos`, `cubicacionDisponibleAt`, `errorCubicacion`, `avisosCubicacion` | Cola de la cubicación en segundo plano de la licitación (script `04`, HU-023.5): `pendiente`/`cubicada`/`fallida` (NULL = se cubicó al guardarla), intentos, desde cuándo se puede tomar, último error y avisos del cubicador (JSON) | Captura de la licitación, worker de cubicación | Worker, API |

## 5. `CotizacionVehiculosLineaSolicitud` — el camión

Una fila por camión. Si el cubicador recomienda 7 camiones para una ruta, son 7 filas. Nexo costea cada camión por separado.

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `id` | Id interno del camión | Captura | Agregado |
| `lineaSolicitudId` | Ruta del camión | Captura | Agregado |
| `idRecomendacionVehiculo` | Fila de la recomendación del cubicador; NULL si no salió de una | Captura (cubicador) | Trazabilidad con el cubicador |
| `tipoVehiculo`, `carroceria` | Código del vehículo (por ejemplo `3S3`) y carrocería que VIAJA: el del cubicador, el elegido por el comercial cuando la ruta va al respaldo (HU-032, reemplaza la decisión 7) o el que puso la Mesa de Costeo de Nexo. NULL = sin vehículo (lo resuelve la Mesa) | Captura; polling (si la Mesa los pone o cambia) | Envío a Nexo, emparejamiento, PDF |
| `pesoKg` | Peso total del camión | Captura | Nexo, emparejamiento |
| `costoFlete`, `costoRetornoContenedor` | Lo que cobra Nexo por el camión | Polling | Totales de la ruta |
| `costoLevanteContenedor` | Copia del levante de la ruta | Captura, `CambiarValorLevante` | Totales de la ruta |
| `lineaSolicitudNexoId` | Línea de Nexo emparejada con este camión | Polling (emparejamiento) | Polling, envío de conexos |
| `estadoCosteo` | Estado del camión (los 8 estados). `descartada` = la Mesa borró su línea; no cuenta en totales ni se reenvía. `rechazada` = la Mesa rechazó su línea (script `10`, HU-028) | Envío, polling | Resumen de la ruta |
| `motivoRechazo`, `rechazadaAt` | Motivo del rechazo que mandó Nexo y cuándo lo vio Cotifly (script `10`). Solo con el camión `rechazada` | Polling | API, asistente |
| `ordenVehiculo` | Orden del camión en su ruta, 1..n (script `03`); el primero lleva los conexos en Nexo. NULL en filas anteriores (se ordenan por `createdAt`) | Captura, recálculo, re-solicitud, `-vN` | Orden de los camiones, envío de conexos |
| `volumenTotalM3`, `porcentajeLlenadoVolumen`, `porcentajeLlenadoPeso` | Volumen y llenado del camión según el cubicador (script `02`) | Captura (cubicador) | API, PDF |
| `createdAt`, `updatedAt` | Marcas de tiempo; antes de `ordenVehiculo`, `createdAt` también llevaba el orden (`ahora + orden × 1 ms`) | Interceptor de EF | Orden de las filas anteriores al `03` |

## 6. `CotizacionMercancias` — lo que se va a mandar (ex `CotizacionPosiciones`)

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `id` | Id interno | Captura | Agregado |
| `lineaSolicitudId`, `orden` | Ruta y posición (única por ruta) | Captura | Agregado |
| `codigoMercancia` | Código del tipo de mercancía del catálogo del cubicador (`GET /web/tipos-mercancia`), por ejemplo `BEBIDAS`. Reemplaza a `idTipoMercancia` desde el `02`. La captura lo exige solo en las mercancías cubicables (con medidas) con el cubicador configurado; NULL en las no cubicables (HU-032) | Captura | Cubicador |
| `nombreMercancia` | Copia del nombre del catálogo (si el catálogo no responde, el que mandó el cliente) | Captura | PDF, asistente |
| `cantidad`, `largoCm`, `anchoCm`, `altoCm`, `pesoKg` | Unidades y medidas. Desde el script `12` (HU-032) las tres medidas pueden ser NULL juntas: mercancía "no cubicable" (solo descripción, cantidad o peso) | Captura | Cubicador; posiciones hacia Nexo cuando la ruta tiene un solo camión |
| `createdAt` | Marca de tiempo | Interceptor de EF | — |

> El detalle de cada ajuste (`02` a `22`) está en la cabecera de su script en `db/scripts/`; los tipos y restricciones, en
> `ESQUEMA_OBJETIVO_SQLSERVER.md` §A.4 y en `db/esquema/cotifly_esquema.sql`.

## 7. `CotizacionServiciosConexos` — conexos de la ruta

Son de la ruta, no del camión: la cantidad ya dice cuántos. Reemplazan el JSON `serviciosConexos` (decisión 3).

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `id` | Id interno | Captura | Agregado |
| `lineaSolicitudId` | Ruta | Captura, ajustes | Agregado |
| `idServicioConexo` | Id del catálogo de Nexo (capturados o copiados a una `-vN`) o id de la fila de la cotización de Nexo (agregados o importados desde Nexo). Único por ruta | Captura, `POST`/`PUT .../servicios-conexos`, polling (#124) | Envío diferido a Nexo (`clsResolutorConexosCatalogo`) |
| `nombreServicioConexo` | Copia del nombre (0010) | Igual | PDF |
| `precio` | Costo unitario de Nexo al momento de capturar, antes del margen. No se vuelve a cotizar | Captura (catálogo de Nexo), `-vN` (catálogo refrescado) | Totales y precio de la ruta |
| `precioVenta` | Precio de venta al cliente (HU-043.1, #245; script `19`). En las filas existentes arranca igual a `precio` (backfill); de ahí en adelante lo ajusta lo que decida HU-043.2 | Captura (hasta HU-043.2: igual a `precio`) | HU-043.2 (pendiente) |
| `codigoServicio` | Código del servicio en el catálogo de Nexo (p. ej. `AVT_AUTO`; script `21`, HU-046.2 #268). Con él se sabe qué medida de la política de seguros cumple el conexo, porque en Nexo cada trayecto tiene su propio id. NULL en las filas anteriores al `21` | Captura y edición de conexos (HU-046.3, pendiente) | Política de seguros de la ruta (HU-046.3) |
| `cantidad` | Cantidad | Captura, `PUT` | Totales, Nexo |
| `idServicioConexoCatalogo` | Id del catálogo de Nexo del conexo (script `03`, HU-014 #188). NULL en filas anteriores (se resuelven por id o por nombre) | Captura, importación desde Nexo, `-vN` | `clsResolutorConexosCatalogo` |
| `categoriaServicioConexo` | Categoría del catálogo de Nexo (`logistico`, `adicional`...; script `05`). NULL en filas anteriores (sale del catálogo al armar el costeo) | Captura, importación, `-vN` | Costeo |
| `createdAt`, `updatedAt` | Marcas de tiempo | Interceptor de EF | — |

Quitar un conexo borra su fila (no hay columna `incluido`). El estado del envío a Nexo (`pendiente`, `enviado`, `incierto`,
`fallido_definitivo`) no tiene columna: se lee de los registros `conexos_envio_nexo` de `EventosHerramienta`.

## 8. Otras tablas de la cotización

### `CotizacionArchivosLicitacion` — el Excel del pliego

Una fila por licitación (la PK es la cabecera).

| Columna | Qué es |
|---|---|
| `cotizacionId` | Cabecera de la licitación (PK y FK a `CotizacionesSolicitud`) |
| `nombreArchivo`, `tipoContenido`, `hoja` | Nombre del archivo, tipo MIME y hoja que se leyó |
| `tamanoBytes`, `sha256`, `contenido` | Tamaño (1 byte a 10 MB, el límite de Nexo; CHECK con el largo real del contenido), huella y el archivo original |
| `origenLectura` | Cómo se leyó el Excel: `analizar_excel` (cada fila es una fila de la hoja) o `leer_estructura` (la estructura que reorganizó Nexo: la exportación arma la hoja "Licitación" y no toca el original). NULL = `analizar_excel`. CHECK de valores (script `22`, HU-044.7 #290). Sin collation explícita: toma la de la base |
| `createdAt` | Cuándo se guardó |

### `CotizacionPdfsHistorial`, `MargenAjustes`, `Correos` y `CorreoDestinatarios`

| Tabla.columna | Qué es |
|---|---|
| `CotizacionPdfsHistorial.id`, `cotizacionId` | Fila del historial y su cabecera |
| `CotizacionPdfsHistorial.pdfPath`, `motivo` | PDF anterior (el archivo no se borra) y por qué se invalidó: `ajuste_margen`, `servicios_conexos`, `textos`, `re_solicitud` |
| `CotizacionPdfsHistorial.usuarioId`, `createdAt` | Quién lo invalidó y cuándo |
| `MargenAjustes.id`, `cotizacionId`, `folio`, `lineaSolicitudId` | El ajuste, su cabecera (y su folio, copiado) y la ruta ajustada |
| `MargenAjustes.margenPolitica`, `margenAplicado`, `diferencia`, `motivo` | Margen sugerido de la ruta, el que puso el comercial, la diferencia y el motivo. Base de las métricas por ruta |
| `MargenAjustes.usuarioId`, `createdAt` | Quién y cuándo |
| `Correos.id`, `cotizacionId`, `asunto`, `messageId` | Un envío del PDF al cliente, su cabecera, el asunto y el id del mensaje |
| `Correos.confirmadoPor` | Usuario que confirmó el envío |
| `Correos.estado`, `error` | `encolado`, `enviando`, `enviado` o `fallido` (CHECK `ck_correos_estado`, script `13`) y el último error |
| `Correos.idMailSender` | Id del correo en MailSender (índice único filtrado `ux_correos_idMailSender`, script `13`, HU-033 #199) |
| `Correos.redirigidoA` | Buzón de pruebas al que se redirigió fuera de producción; los destinatarios guardados siguen siendo los originales (`13`) |
| `Correos.intentosEntrega`, `enviadoAt` | Intentos de entrega al relay (`attemptCount` de MailSender) y cuándo el relay lo aceptó (`13`) |
| `Correos.estadoActualizadoAt`, `siguienteConsultaAt`, `encoladoAt` | Última consulta del estado, agenda del worker de correos (NULL = estado final; índice filtrado) y última aceptación de MailSender (NULL = se usa `createdAt`) (`13`) |
| `Correos.createdAt` | Cuándo se pidió el envío |
| `CorreoDestinatarios.id`, `correoId`, `email`, `tipo` | Destinatario de un correo: `para` o `cc` (que haya al menos un `para` lo valida la aplicación) |

## 9. Idempotencia, identidad y conversación

| Tabla.columna | Qué es |
|---|---|
| `CapturasIdempotentes.id`, `usuarioId`, `operacion`, `clave` | Una fila por `Idempotency-Key` (8 a 64 caracteres, comparación exacta), comercial y operación (`capturar_costeo`, `conexos`, `re_solicitar_costeo`, `editar_solicitud`); único `(usuarioId, operacion, clave)` (script `06`, #191) |
| `CapturasIdempotentes.huella` | SHA-256 del cuerpo (los números se comparan por valor). Misma clave con otra huella = 409 |
| `CapturasIdempotentes.estado`, `tomadaPor`, `enCursoHasta` | `en_curso` (la tiene la petición `tomadaPor` hasta `enCursoHasta`, 10 min; después otra la retoma), `terminada` o `libre` |
| `CapturasIdempotentes.resultado` | JSON de la respuesta que consumió la clave (obligatorio en `terminada`) |
| `CapturasIdempotentes.cotizacionId`, `folio`, `solicitudNexoId`, `scorePrioridad` | La captura en curso: el folio confirmado y lo que devolvió Nexo, para que un reintento no vuelva a crear la solicitud |
| `CapturasIdempotentes.createdAt`, `updatedAt`, `expiraAt` | Marcas de tiempo y vencimiento (24 h); la API borra las vencidas |
| `UsuariosWeb.id`, `idUsuarioExterno` | Id propio de Cotifly y el de ClientePro (`tblSAPUsuariosApp.ID`, único) |
| `UsuariosWeb.nombreUsuario`, `nombreCompleto`, `email`, `telefono`, `regional` | Datos del usuario que trae ClientePro (`nombreUsuario` único) |
| `UsuariosWeb.createdAt`, `ultimaSesion` | Alta y último ingreso |
| `Roles.id`, `codigo`, `descripcion`, `activo`, `updatedAt` | Rol interno: `comercial`, `gerente_comercial` o `director` (CHECK) |
| `Roles.idRolExterno` | `IDROLES` de ClientePro con el que se entra con ese rol (único; NULL = nadie entra con él). Lo fija el DBA por ambiente |
| `UsuariosRoles.usuarioId`, `rolId`, `activo`, `asignadoAt` | Rol asignado a un usuario. Varias asignaciones activas por usuario (multi-rol), pero el mismo rol no se repite (script `16`) |
| `UsuariosRoles.revisadoPor`, `revisadoAt` | Quién revisó la asignación y cuándo |
| `TokensRefresco.id`, `usuarioId`, `tokenHash` | Refresh token: solo el SHA-256 en hexadecimal (collation `BIN2`, único) |
| `TokensRefresco.familiaId`, `reemplazadoPorId`, `revocadoAt` | Cadena de rotación: reusar un token ya rotado revoca toda la familia |
| `TokensRefresco.createdAt`, `expiraAt` | Emisión y vencimiento (CHECK `expiraAt > createdAt`) |
| `TokensRefresco.rolSesion` | Rol interno con el que se emitió la sesión; el refresh lo conserva y revalida (CHECK con los tres códigos; NULL = emitido antes del script `16`, HU-040.1 #229) |
| `TokensRevocados.jti`, `usuarioId`, `expiraAt`, `createdAt` | Access tokens revocados en el logout, hasta su vencimiento |
| `Sesiones.spaceId`, `threadId`, `usuarioId` | Hilo del asistente (Google Chat o consola web) y su usuario |
| `Sesiones.estado`, `estadoAgente`, `ultimaActividad` | `activa` o `archivada`, JSON del slot-filling y última actividad |
| `EventosConversacion.*` | Mensajes del hilo: `rol`, `contenido`, `herramientas` (JSON), usuario y fecha |
| `EventosHerramienta.*` | Auditoría de herramientas y registros de proceso (`conexos_envio_nexo`, `conexos_pendientes_nexo`, `cambiar_servicios_conexos`): `herramienta`, `input` y `outputResumen` (JSON, sin montos al asistente), `exito`, `duracionMs`, `threadId`, `createdAt` |

## 10. Configuración

| Tabla.columna | Qué es |
|---|---|
| `MargenesPolitica.id`, `lineaNegocio` | Línea de negocio (única). Desde el `0009`: Nacional, Distribucion, Importacion, Exportacion, Internacional y DevolucionContenedor. Las siembra `db/seeder/01_lineas_negocio.sql` |
| `MargenesPolitica.margen`, `metodoCalculo` | Margen de política (fracción) y método (`margen`) |
| `MargenesPolitica.mapeoLineasNexo` | Nombre equivalente en el enum `LineaNegocio` de Nexo (NULL a propósito en Distribucion) |
| `MargenesPolitica.isActive`, `updatedAt` | Vigencia y última modificación |
| `SapMapeoTrqType.trqType`, `salesOrg` | TrqType de SAP TM y organización de ventas (PK; `salesOrg = ''` es el comodín: gana la fila específica si existe) |
| `SapMapeoTrqType.lineaNegocioId` | Línea de negocio (FK a `MargenesPolitica.id`, script `18`, HU-042.4 #247). El backend resuelve por este id |
| `SapMapeoTrqType.lineaNegocio` | El texto original de la línea; se conserva mientras el backend lo lea y se retira en un script futuro |
| `SapMapeoTrqType.activo` | Vigencia. Falta el TrqType de `DevolucionContenedor` (pendiente externo: lo da negocio o SAP) |
| `TarifasSeguro.id`, `tasa`, `vigenteDesde`, `isActive`, `updatedAt` | Tasa del seguro de la mercancía con su historia (script `20`, HU-045.1 #263): `tasa` es una fracción (CHECK > 0 y < 1; la inicial, 0.003450 = 0,345 %, la siembra `db/seeder/04_tarifa_seguro.sql`). Una sola activa (índice único filtrado); las viejas se desactivan. Se cambia con `db/scripts/operacion/cambiar_tasa_seguro.sql` |
| `PoliticaSeguroTramos.id`, `nombre`, `valorDesde`, `valorHasta`, `isActive`, `updatedAt` | Tramo de valor de la mercancía de la ruta (script `21`, HU-046.2 #268): `valorDesde` exclusivo, `valorHasta` incluido (NULL = sin tope), en pesos fijos. Los activos son contiguos y sin solapes (lo valida `db/seeder/05_politica_seguros.sql`) |
| `PoliticaSeguroMedidas.id`, `codigo`, `nombre`, `esCobrable`, `isActive`, `updatedAt` | Medida de la política: `codigo` único (`esquema_basico`, `gps`, `candado_satelital`, `restriccion_nocturna`, `acompanante_vehicular`, `acompanante_total`, `incluir_seguro`); `esCobrable` = se cumple agregando un conexo de Nexo (los acompañantes) |
| `PoliticaSeguroTramoMedidas.id`, `tramoId`, `medidaId`, `nivel`, `updatedAt` | Qué medida aplica en cada tramo y con qué `nivel` (`recomendada` u `obligatoria`); único `(tramoId, medidaId)`. Solo los acompañantes son obligatorios |
| `PoliticaSeguroMedidaConexos.id`, `medidaId`, `codigoServicio`, `updatedAt` | Códigos del catálogo de Nexo que cumplen cada medida (sin FK: es otra base); la obligación se cumple con cualquiera. `acompanante_vehicular`: los 5 acompañantes; `acompanante_total`: los 3 `AVT_*` |
| `SapMargenCliente.id`, `salesOrgId`, `trqType`, `bpSap` | Copia local del margen recomendado de SAP TM (`RentabClienteSet`, script `17`, HU-042.1 #242): una fila por `(salesOrgId, trqType, bpSap)` (única) con `id` propio; `bpSap` es el BP del cliente en SAP (`OrderPartyId`) |
| `SapMargenCliente.nit` | NIT ya resuelto por BP contra el DW (`DWCubosOperacionesEBS.dbo.DimBPSolicitante`), para cruzar directo con `nitCliente` |
| `SapMargenCliente.margenRecomendado`, `activo` | El valor completo que manda SAP y si SAP lo sigue trayendo (si deja de traerlo se desactiva, no se borra) |
| `SapMargenCliente.creadoAt`, `actualizadoAt` | Alta y última actualización |
| `SapMargenSyncLog.*` | Log de corridas de la sincronización (una fila por ejecución): `iniciadoAt`, `finalizadoAt`, `filasInsertadas`, `filasActualizadas`, `filasDesactivadas`, `status` (`ok`, `error`, `omitido`) y `mensajeError` |

La copia local del margen la sincroniza el backend a diario (`clsWorkerSincronizacionMargenSap`, HU-042.2 #243, ADR-027), que
llama al procedimiento `dbo_cotifly.sp_sincronizar_margen_sap` (el `MERGE` y el log, en una transacción con `sp_getapplock`).
Las columnas de `SapMargenCliente` y `SapMargenSyncLog` y `CotizacionArchivosLicitacion.origenLectura` no llevan collation
explícita: toman la de la base, como las crean los ajustes `17` y `22`.
