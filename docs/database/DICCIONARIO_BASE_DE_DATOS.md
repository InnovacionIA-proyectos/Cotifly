# Diccionario de datos — `dbo_cotifly` (SQL Server)

> **Estado:** vigente desde HU-021 · **Dueño:** Duban Guerra (backend) y scalderon-bs (datos) · **Actualizado:** 2026-10-28 (`SapMargenCliente`/`SapMargenSyncLog`, HU-042.1 #242; `SapMapeoTrqType.lineaNegocioId`, HU-042.4, #247)

Qué significa cada columna de las tablas de cotizaciones, quién la escribe y quién la lee. Los tipos, nulos, restricciones e
índices están en [`ESQUEMA_OBJETIVO_SQLSERVER.md`](ESQUEMA_OBJETIVO_SQLSERVER.md) (Parte A, con el diagrama ER); la decisión
de diseño, en [ADR-019](../../backend-dotnet/specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md). Fuente de verdad:
`db/scripts/01_crear_esquema.sql` (= `0001` a `0010`), los ajustes `db/scripts/02` a `13` y `db/scripts/pendientes/04`, y el
modelo EF
(`backend-dotnet/src/Cotifly.Infraestructura/Comun/Persistence/Configurations/`).

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
| `cotizacionSolicitudId`, `orden` | Cabecera y posición de la ruta (1..N) | Captura | API (`linea_orden`), PDF |
| `rutaOrigenIndice` | Posición en el formulario o fila del Excel (base 0) | Captura | Reporte de errores de la licitación, Excel exportado |
| `lineaNegocioId` | Línea de negocio de la ruta (`MargenesPolitica`). Cada ruta puede tener la suya | Captura | Margen de política, `lineaNegocio` de cada camión en Nexo, métricas por ruta |
| `origenMunicipioId`, `destinoMunicipioId` | Id del municipio en Nexo (`tbl_municipios`), obligatorio en toda captura nueva (decisión 2) | Captura (catálogo `GET /web/municipios`) | Envío a Nexo |
| `origenNombre`, `destinoNombre` | Copia del nombre del catálogo, para mostrar y para que la cotización no cambie si cambia el catálogo (0010) | Captura | PDF, emparejamiento, asistente |
| `ciudadDevolucionMunicipioId` / `Nombre`, `ciudadLevanteMunicipioId` / `Nombre` | Ciudades del contenedor, id + nombre (decisión 2) | Captura | Nexo, PDF |
| `tipologia`, `claseViaje`, `claseContenedor`, `exclusivo`, `consolidado`, `retornaContenedor`, `levanteContenedorDiferente` | Condiciones del viaje y del contenedor (catálogos de Nexo) | Captura | Envío a Nexo |
| `horasCargue`, `horasDescargue` | Horas de la ruta (0 a 240) | Captura | Nexo |
| `pesoKg` | Peso total declarado de la ruta | Captura | Cubicador, respaldo de un camión |
| `pesoConTaraKg` | Peso total de la ruta CON la tara de los contenedores (`decimal(12,2)`, NULL = sin tara; solo en una ruta `Contenedor`, nunca menor que la carga; script `11`, HU-030.1 #184) | Captura, edición | Nexo: peso de las líneas de la ruta (la tara repartida por partes iguales entre los camiones; esas líneas van sin posiciones). No va al cubicador |
| `valorMercancia` | Valor de la mercancía (para el seguro; no es costo) | Captura | Nexo |
| `valorLevanteContenedor` | Un levante; lo digita el comercial y se copia a cada camión (decisión 4) | Captura, `CambiarValorLevante` | Camiones |
| `costoFletesTotal`, `costoRetornoContenedorTotal`, `costoLevanteContenedorTotal` | Suma de los camiones que cuentan | Dominio (polling) | Precio |
| `costoServiciosConexos` | Σ precio × cantidad de los conexos | Dominio | Precio |
| `costoFinal` | Fletes + retorno + levante + conexos | Dominio | Precio, métricas |
| `margenPoliticaSugerido`, `margenSAPSugerido` | Margen sugerido por la política de la línea y por SAP TM (fracción). El vigente es el de SAP si existe | Envío (resolución del margen) | Ajuste de margen, asistente |
| `margenAplicado`, `origenMargen`, `metodoCalculo` | Margen con el que se calcula el precio, de dónde salió (`sap_tm`, `politica_linea`, `negociado`) y el método (`margen` o `markup`) | Envío; ajuste por ruta (decisiones 6 y 9) | Precio, métricas |
| `precioFinalLinea` | `costoFinal` con el margen, en la moneda de la cabecera | Dominio | PDF, cabecera, métricas |
| `estadoCosteo` | Resumen del estado de sus camiones (8 estados; `rechazada` si lo están todos sus camiones vigentes, script `10`) | Dominio | Cabecera, API |
| `tipoVehiculoElegido`, `carroceriaElegida` | Vehículo que eligió el comercial en la captura: respaldo del cubicador (script `12`, HU-032, ADR-022). Se conserva en la edición y la re-solicitud aunque el cubicador o la Mesa cambien el del camión. NULL = no eligió | Captura, edición | Planificador de camiones (respaldo), API |
| `idRecomendacion`, `resultadoCubicacion`, `motivoSinSeleccion`, `condicionada` | Resultado de la cubicación de la ruta (script `02`): la corrida del cubicador, `ASIGNADO` o `SIN_SELECCION_AUTOMATICA`, el motivo (5 valores, con `BUSQUEDA_AGOTADA` del `07`) y si lleva mercancías condicionadas | Captura, recálculo | API, Nexo |
| `estadoCubicacion`, `cubicacionIntentos`, `cubicacionDisponibleAt`, `errorCubicacion`, `avisosCubicacion` | Cola de la cubicación en segundo plano de la licitación (`pendientes/04`, HU-023.5): `pendiente`/`cubicada`/`fallida`, intentos, desde cuándo se puede tomar, último error y avisos del cubicador (JSON) | Captura de la licitación, worker de cubicación | Worker, API |

## 5. `CotizacionVehiculosLineaSolicitud` — el camión

Una fila por camión. Si el cubicador recomienda 7 camiones para una ruta, son 7 filas. Nexo costea cada camión por separado.

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
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
| `createdAt` | Marca de tiempo; antes de `ordenVehiculo` también llevaba el orden (`ahora + orden × 1 ms`) | Interceptor de EF | Orden de las filas anteriores al `03` |

## 6. `CotizacionMercancias` — lo que se va a mandar (ex `CotizacionPosiciones`)

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `lineaSolicitudId`, `orden` | Ruta y posición | Captura | Agregado |
| `codigoMercancia` | Código del tipo de mercancía del catálogo del cubicador (`GET /web/tipos-mercancia`), por ejemplo `BEBIDAS`. Reemplaza a `idTipoMercancia` desde el `02`. La captura lo exige solo en las mercancías cubicables (con medidas) con el cubicador configurado; NULL en las no cubicables (HU-032) | Captura | Cubicador |
| `nombreMercancia` | Copia del nombre del catálogo (si el catálogo no responde, el que mandó el cliente) | Captura | PDF, asistente |
| `cantidad`, `largoCm`, `anchoCm`, `altoCm`, `pesoKg` | Unidades y medidas. Desde el script `12` (HU-032) las tres medidas pueden ser NULL juntas: mercancía "no cubicable" (solo descripción, cantidad o peso) | Captura | Cubicador; posiciones hacia Nexo cuando la ruta tiene un solo camión |

> El detalle de cada ajuste (`02`, `03`, `pendientes/04`, `05`, `10`, `11`, `12`, `13`) está en la cabecera de su script en
> `db/scripts/`; los tipos y restricciones, en `ESQUEMA_OBJETIVO_SQLSERVER.md` §A.4.

## 7. `CotizacionServiciosConexos` — conexos de la ruta

Son de la ruta, no del camión: la cantidad ya dice cuántos. Reemplazan el JSON `serviciosConexos` (decisión 3).

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `lineaSolicitudId` | Ruta | Captura, ajustes | Agregado |
| `idServicioConexo` | Id del catálogo de Nexo (capturados o copiados a una `-vN`) o id de la fila de la cotización de Nexo (agregados o importados desde Nexo). Único por ruta | Captura, `POST`/`PUT .../servicios-conexos`, polling (#124) | Envío diferido a Nexo (`clsResolutorConexosCatalogo`) |
| `nombreServicioConexo` | Copia del nombre (0010) | Igual | PDF |
| `precio` | Costo unitario de Nexo al momento de capturar, antes del margen. No se vuelve a cotizar | Captura (catálogo de Nexo), `-vN` (catálogo refrescado) | Totales y precio de la ruta |
| `cantidad` | Cantidad | Captura, `PUT` | Totales, Nexo |
| `idServicioConexoCatalogo` | Id del catálogo de Nexo del conexo (script `03`, HU-014 #188). NULL en filas anteriores (se resuelven por id o por nombre) | Captura, importación desde Nexo, `-vN` | `clsResolutorConexosCatalogo` |
| `categoriaServicioConexo` | Categoría del catálogo de Nexo (`logistico`, `adicional`...; script `05`). NULL en filas anteriores (sale del catálogo al armar el costeo) | Captura, importación, `-vN` | Costeo |

Quitar un conexo borra su fila (no hay columna `incluido`). El estado del envío a Nexo (`pendiente`, `enviado`, `incierto`,
`fallido_definitivo`) no tiene columna: se lee de los registros `conexos_envio_nexo` de `EventosHerramienta`.

## 8. Otras tablas

| Tabla | Qué guarda |
|---|---|
| `MargenAjustes` | Cada ajuste de margen de un comercial: `margenPolitica` (el sugerido de la ruta), `margenAplicado`, `diferencia`, `motivo` y la ruta (`lineaSolicitudId`). Base de las métricas por ruta |
| `CotizacionArchivosLicitacion` | El Excel original del pliego (hasta 10 MB), con su SHA-256 y la hoja usada |
| `CotizacionPdfsHistorial` | Los PDF anteriores de una cotización y por qué se invalidaron (`ajuste_margen`, `servicios_conexos`, `textos`, `re_solicitud`) |
| `Correos`, `CorreoDestinatarios` | Cada envío del PDF al cliente, su estado y sus destinatarios (`para`, `cc`). Desde el script `13` (HU-033, MailSender): `estado` `encolado` \| `enviando` \| `enviado` \| `fallido` (CHECK), `idMailSender` (id del correo en MailSender, único), `redirigidoA` (buzón de pruebas fuera de producción; los destinatarios guardados siguen siendo los originales), `intentosEntrega`, `enviadoAt`, `estadoActualizadoAt`, `siguienteConsultaAt` (agenda del worker; NULL = estado final) y `encoladoAt` (última aceptación de MailSender) |
| `CapturasIdempotentes` | Claves `Idempotency-Key` persistentes (script `06`, #191): una fila por clave, comercial y operación (`capturar_costeo`, `conexos`, `re_solicitar_costeo`, `editar_solicitud`), con la huella del cuerpo, el estado (`en_curso`, `terminada`, `libre`), la respuesta guardada y, en la captura, el folio y el `solicitudNexoId` para que un reintento no vuelva a crear la solicitud en Nexo. Vencen a las 24 h; la API las purga |
| `EventosHerramienta` | Auditoría de herramientas y registros de proceso: `conexos_envio_nexo`, `conexos_pendientes_nexo`, `cambiar_servicios_conexos` (JSON en `input`/`outputResumen`, sin montos al asistente) |
| `EventosConversacion`, `Sesiones` | Historial y estado del asistente por folio |
| `UsuariosWeb`, `Roles`, `UsuariosRoles`, `TokensRefresco`, `TokensRevocados` | Identidad: usuarios de ClientePro, rol interno, refresh tokens (hash) y access tokens revocados |
| `MargenesPolitica` | Líneas de negocio, margen de política y nombre equivalente en Nexo (`mapeoLineasNexo`). Desde el `0009`: Nacional, Distribucion, Importacion, Exportacion, Internacional y DevolucionContenedor (salen ITR, Almacenamiento y Almacenamiento3PL) |
| `SapMapeoTrqType` | TrqType de SAP TM → línea de negocio. Clave `(trqType, salesOrg)`; `salesOrg=''` es el comodín (gana la fila específica si existe, `salesOrg DESC`). `lineaNegocioId` (FK a `MargenesPolitica.id`, script `18`, HU-042.4, #247): hoy conviven con `lineaNegocio` (texto, el original) mientras el backend se adapta a leer por id en vez de por texto — `lineaNegocio` se retira en un script futuro. Falta el TrqType de `DevolucionContenedor` (lo da negocio o SAP) |
| `SapMargenCliente` | Copia local del margen recomendado de SAP TM (`RentabClienteSet`), sincronizada a diario por un job fuera de `backend-dotnet` (HU-042.1, #242; no la lee el backend todavía, eso es HU-042.2, #243). Clave de negocio única `(salesOrgId, trqType, bpSap)` + `id` propio; `bpSap` es el BP del cliente en SAP (`OrderPartyId`); `nit` ya resuelto por BP contra el DW `DWCubosOperacionesEBS.dbo.DimBPSolicitante`, para cruzar directo con `nitCliente` de `CotizacionesSolicitud` sin que el backend tenga que resolverlo aparte; `margenRecomendado` es el valor completo que manda SAP; `activo` es soft delete (si SAP deja de traer un combo, se desactiva, no se borra) |
| `SapMargenSyncLog` | Log de corridas del job de `SapMargenCliente` (HU-042.1, #242): append-only, una fila por ejecución (no por registro de SAP), con cuándo empezó/terminó, cuántas filas insertó/actualizó/desactivó, y el resultado (`ok`/`error`/`omitido`) |
