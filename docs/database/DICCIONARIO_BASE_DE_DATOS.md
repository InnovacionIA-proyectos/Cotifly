# Diccionario de datos — `dbo_cotifly` (SQL Server)

> **Estado:** vigente desde HU-021 · **Dueño:** Duban Guerra (backend) y scalderon-bs (datos) · **Actualizado:** 2026-10-13 (HU-021.14)

Qué significa cada columna de las tablas de cotizaciones, quién la escribe y quién la lee. Los tipos, nulos, restricciones e
índices están en [`ESQUEMA_OBJETIVO_SQLSERVER.md`](ESQUEMA_OBJETIVO_SQLSERVER.md) (Parte A, con el diagrama ER); la decisión
de diseño, en [ADR-019](../../backend-dotnet/specs/decisiones/ADR-019-reestructuracion-rutas-y-camiones.md). Fuente de verdad:
`db/scripts/01_crear_esquema.sql` (= `0001` a `0010`) y el modelo EF
(`backend-dotnet/src/Cotifly.Infraestructura/Comun/Persistence/Configurations/`).

## 1. Para qué sirve esta base

Es la base de **trazabilidad** de Cotifly, no el ERP. Guarda la cotización completa: quién la pidió, qué rutas y camiones
tiene, qué costó cada camión en Nexo, con qué margen se le puso precio a cada ruta, qué PDF y qué correo salieron, y si el
negocio se ganó. Nexo (costos y conexos), SAP TM (margen) y el cubicador (vehículos) son fuentes externas: de ellas queda
solo la foto de lo que se usó.

## 2. Cómo se llena una cotización

1. **Captura** (`POST /web/solicitudes-costeo` o licitación por Excel): se crea la cabecera (`CotizacionesSolicitud`) con
   una fila por ruta (`CotizacionLineasSolicitud`), sus mercancías y sus conexos. El cubicador recomienda los camiones de
   cada ruta y se crea una fila por camión (`CotizacionVehiculosLineaSolicitud`). Sin cubicador, la ruta queda con un
   camión sin vehículo que resuelve la Mesa de Costeo de Nexo. Cubica el sistema donde nace la solicitud: si nace en
   Cotifly, Cotifly llama al cubicador; Nexo no vuelve a cubicar lo que le llega de Cotifly.
2. **Envío a Nexo:** una línea de Nexo por camión. La cabecera guarda `solicitudNexoId`.
3. **Polling:** empareja cada camión con su línea de Nexo (`lineaSolicitudNexoId`), guarda su flete, retorno y estado, y
   la ruta recalcula sus totales y su precio. Los conexos se envían a Nexo una sola vez, pegados al primer camión vigente
   de la ruta (decisión 10); el registro de cada envío va en `EventosHerramienta`.
4. **Ajustes del comercial** (margen por ruta, conexos, levante, textos): suben `versionNegocio`, recalculan la ruta y la
   cabecera y siguen la regla del PDF (ADR-018). El ajuste de margen queda en `MargenAjustes` con su ruta.
5. **PDF y correo:** `pdfPath`, `CotizacionPdfsHistorial`, `Correos` y `CorreoDestinatarios`.
6. **Resultado:** `ganada`, `motivoRechazo`, `marcadoAt`.

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
| `cotizacionNexoId` | Id de la **cotización** de Nexo | Polling | Conexos, resultado en Nexo |
| `costeoEnviadoAt`, `costeoSiguienteConsultaAt`, `costeoConsultasFallidas`, `costeoListoAt` | Agenda y seguimiento del polling | Envío, polling | Worker del polling |
| `versionNegocio` | Versión de negocio: viaja como `ETag` y vuelve en `If-Match` (428/409) | Cada cambio que ve el comercial y el polling cuando cambia algo | Concurrencia optimista (ADR-018) |
| `version` | Número de versión del folio (`-vN`) | Nueva versión | Folio, asistente |
| `pdfPath`, `pdfDesactualizado` | PDF vigente y si un cambio lo invalidó | Generación del PDF; mutaciones | PDF, correo |
| `textosCotizacion` | JSON con asunto e introducción del PDF (IA con respaldo estándar) | Redacción (ADR-018) | PDF, correo |
| `estadoEnvio` | `sin_enviar`, `enviada`, `reenviada` | Correo | Regla del PDF, métricas, vistas |
| `ganada`, `motivoRechazo`, `marcadoAt` | Resultado del negocio | Seguimiento | Métricas |
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
| `valorMercancia` | Valor de la mercancía (para el seguro; no es costo) | Captura | Nexo |
| `valorLevanteContenedor` | Un levante; lo digita el comercial y se copia a cada camión (decisión 4) | Captura, `CambiarValorLevante` | Camiones |
| `costoFletesTotal`, `costoRetornoContenedorTotal`, `costoLevanteContenedorTotal` | Suma de los camiones que cuentan | Dominio (polling) | Precio |
| `costoServiciosConexos` | Σ precio × cantidad de los conexos | Dominio | Precio |
| `costoFinal` | Fletes + retorno + levante + conexos | Dominio | Precio, métricas |
| `margenPoliticaSugerido`, `margenSAPSugerido` | Margen sugerido por la política de la línea y por SAP TM (fracción). El vigente es el de SAP si existe | Envío (resolución del margen) | Ajuste de margen, asistente |
| `margenAplicado`, `origenMargen`, `metodoCalculo` | Margen con el que se calcula el precio, de dónde salió (`sap_tm`, `politica_linea`, `negociado`) y el método (`margen` o `markup`) | Envío; ajuste por ruta (decisiones 6 y 9) | Precio, métricas |
| `precioFinalLinea` | `costoFinal` con el margen, en la moneda de la cabecera | Dominio | PDF, cabecera, métricas |
| `estadoCosteo` | Resumen del estado de sus camiones | Dominio | Cabecera, API |

## 5. `CotizacionVehiculosLineaSolicitud` — el camión

Una fila por camión. Si el cubicador recomienda 7 camiones para una ruta, son 7 filas. Nexo costea cada camión por separado.

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `lineaSolicitudId` | Ruta del camión | Captura | Agregado |
| `idRecomendacionVehiculo` | Fila de la recomendación del cubicador; NULL si no salió de una | Captura (cubicador) | Trazabilidad con el cubicador |
| `tipoVehiculo`, `carroceria` | Código del vehículo (por ejemplo `3S3`) y carrocería. Los define el cubicador o la Mesa de Costeo de Nexo, no el comercial (decisión 7). NULL = sin selección automática | Captura; polling (si la Mesa los pone o cambia) | Envío a Nexo, emparejamiento, PDF |
| `pesoKg` | Peso total del camión | Captura | Nexo, emparejamiento |
| `costoFlete`, `costoRetornoContenedor` | Lo que cobra Nexo por el camión | Polling | Totales de la ruta |
| `costoLevanteContenedor` | Copia del levante de la ruta | Captura, `CambiarValorLevante` | Totales de la ruta |
| `lineaSolicitudNexoId` | Línea de Nexo emparejada con este camión | Polling (emparejamiento) | Polling, envío de conexos |
| `estadoCosteo` | Estado del camión (los 7 estados). `descartada` = la Mesa borró su línea; no cuenta en totales ni se reenvía | Envío, polling | Resumen de la ruta |
| `createdAt` | Además de la marca de tiempo, lleva el orden de la recomendación (`ahora + orden × 1 ms`) | Interceptor de EF | Orden de los camiones |

## 6. `CotizacionMercancias` — lo que se va a mandar (ex `CotizacionPosiciones`)

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `lineaSolicitudId`, `orden` | Ruta y posición | Captura | Agregado |
| `codigoMercancia` | Código del tipo de mercancía del catálogo del cubicador (`GET /web/tipos-mercancia`), por ejemplo `BEBIDAS`. Reemplaza a `idTipoMercancia` desde el `02`. Obligatorio en toda captura nueva | Captura | Cubicador |
| `nombreMercancia` | Copia del nombre del catálogo (si el catálogo no responde, el que mandó el cliente) | Captura | PDF, asistente |
| `cantidad`, `largoCm`, `anchoCm`, `altoCm`, `pesoKg` | Unidades y medidas | Captura | Cubicador; posiciones hacia Nexo cuando la ruta tiene un solo camión |

> **HU-023:** `db/scripts/02_alter_cubicador.sql` agrega además el resultado de la cubicación en la ruta (`idRecomendacion`,
> `resultadoCubicacion`, `motivoSinSeleccion`, `condicionada`) y el llenado en el camión (`volumenTotalM3`,
> `porcentajeLlenadoVolumen`, `porcentajeLlenadoPeso`). `db/scripts/pendientes/04_cubicacion_segundo_plano.sql` agrega en la
> ruta la cola de la cubicación en segundo plano. El detalle está en la cabecera de cada script.

## 7. `CotizacionServiciosConexos` — conexos de la ruta

Son de la ruta, no del camión: la cantidad ya dice cuántos. Reemplazan el JSON `serviciosConexos` (decisión 3).

| Columna | Qué es | Quién la escribe | Quién la lee |
|---|---|---|---|
| `lineaSolicitudId` | Ruta | Captura, ajustes | Agregado |
| `idServicioConexo` | Id del catálogo de Nexo (capturados o copiados a una `-vN`) o id de la fila de la cotización de Nexo (agregados o importados desde Nexo). Único por ruta | Captura, `POST`/`PUT .../servicios-conexos`, polling (#124) | Envío diferido a Nexo (`clsResolutorConexosCatalogo`) |
| `nombreServicioConexo` | Copia del nombre (0010) | Igual | PDF |
| `precio` | Costo unitario de Nexo al momento de capturar, antes del margen. No se vuelve a cotizar | Captura (catálogo de Nexo), `-vN` (catálogo refrescado) | Totales y precio de la ruta |
| `cantidad` | Cantidad | Captura, `PUT` | Totales, Nexo |

Quitar un conexo borra su fila (no hay columna `incluido`). El estado del envío a Nexo (`pendiente`, `enviado`, `incierto`,
`fallido_definitivo`) no tiene columna: se lee de los registros `conexos_envio_nexo` de `EventosHerramienta`.

## 8. Otras tablas

| Tabla | Qué guarda |
|---|---|
| `MargenAjustes` | Cada ajuste de margen de un comercial: `margenPolitica` (el sugerido de la ruta), `margenAplicado`, `diferencia`, `motivo` y la ruta (`lineaSolicitudId`). Base de `vAjustesMargenPorComercial` y de las métricas por ruta |
| `CotizacionArchivosLicitacion` | El Excel original del pliego (hasta 10 MB), con su SHA-256 y la hoja usada |
| `CotizacionPdfsHistorial` | Los PDF anteriores de una cotización y por qué se invalidaron (`ajuste_margen`, `servicios_conexos`, `textos`, `re_solicitud`) |
| `Correos`, `CorreoDestinatarios` | Cada envío del PDF al cliente, su estado y sus destinatarios (`para`, `cc`) |
| `EventosHerramienta` | Auditoría de herramientas y registros de proceso: `conexos_envio_nexo`, `conexos_pendientes_nexo`, `cambiar_servicios_conexos` (JSON en `input`/`outputResumen`, sin montos al asistente) |
| `EventosConversacion`, `Sesiones` | Historial y estado del asistente por folio |
| `UsuariosWeb`, `Roles`, `UsuariosRoles`, `TokensRefresco`, `TokensRevocados` | Identidad: usuarios de ClientePro, rol interno, refresh tokens (hash) y access tokens revocados |
| `MargenesPolitica` | Líneas de negocio, margen de política y nombre equivalente en Nexo (`mapeoLineasNexo`). Desde el `0009`: Nacional, Distribucion, Importacion, Exportacion, Internacional y DevolucionContenedor (salen ITR, Almacenamiento y Almacenamiento3PL) |
| `SapMapeoTrqType` | TrqType de SAP TM → línea de negocio. Falta el de `DevolucionContenedor` (lo da negocio o SAP) |
