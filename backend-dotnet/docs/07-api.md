# 7. API

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

La fuente de verdad del contrato es [`specs/openapi.yaml`](../specs/openapi.yaml) (OpenAPI 3.0.3, #17, SDD según [ADR-010](../specs/decisiones/ADR-010-metodologia-sdd-tdd-bdd.md)). El resumen para el frontend está en [`docs/docs_front/CONTRATO_API_WEB.md`](../../docs/docs_front/CONTRATO_API_WEB.md). Este documento no repite los esquemas: resume lo implementado, la autenticación, los errores y los límites. Un cambio de contrato empieza siempre en `openapi.yaml`.

Contenido:

- [7.1 Convenciones](#71-convenciones)
- [7.2 Autenticación y autorización](#72-autenticación-y-autorización)
- [7.3 Endpoints](#73-endpoints)
- [7.4 Errores](#74-errores)
- [7.5 Ejemplos](#75-ejemplos)
- [7.6 Límites, CORS y encabezados](#76-límites-cors-y-encabezados)
- [7.7 Verificación del contrato](#77-verificación-del-contrato)

## 7.1 Convenciones

| Grupo | Serialización | Notas |
|---|---|---|
| `/web/*` | snake_case (`clsJsonSnakeCase`, `clsFormateadorEntradaWeb`/`clsFormateadorSalidaWeb`) | Nulos siempre escritos; números estrictos (`"0.2"` como texto es un cuerpo inválido) |
| `/api/auth/*` | camelCase del patrón "Login SSO — Cliente Pro" (`idUsuario`, `debeCambiarPassword`) | Excepción definida por el contrato: `user.nombre_completo` |
| `/health`, `/health/ready`, `/pdfs/*` | JSON por defecto de ASP.NET Core o binario | `/pdfs/*` devuelve `application/pdf` inline |

- Operaciones con `x-estado: borrador` no están congeladas; hoy son solo `/web/metricas/actividad` y `/ajustes-margen`, además obsoletas (`deprecated: true`) desde que HU-010.1 (#45) agregó `/web/metricas/comercial` y `/regional`.
- Las rutas de los controllers son explícitas (`[Route("web/...")]`), nunca `[controller]`.
- `ubicacion` del usuario es `UsuariosWeb.regional`, que el login llena y refresca desde `DimEjecutivo` (#81, ADR-016 §6) cuando `ConnectionStrings__Regional` está configurada. Sale `null` si el usuario no está en esa tabla y el DBA no la llenó, o si no hay conexión. Una falla de `DimEjecutivo` nunca cambia la respuesta del login: se devuelve la regional que ya tenía.

## 7.2 Autenticación y autorización

- Login: `POST /api/auth/login-clientepro` (anónimo). Flujo y verificación doble en [2.9](02-arquitectura.md#29-identidad-login-sso-con-verificación-doble) y [4.2](04-flujo-del-sistema.md#42-inicio-de-sesión-y-renovación).
- Cada llamada a `/web/*` y `/pdfs/*` lleva `Authorization: Bearer <access>` (JWT HS256, 60 minutos; claims `sub`, `jti`, `username`, `rol`, `iss`, `aud`, `iat`, `exp`).
- Sin token, vencido, firmado con otra clave o revocado: `401 {"error":"Inicia sesión de nuevo."}`. Rol insuficiente: `403 {"error":"No tienes permiso para esta operación."}`.
- Políticas: `Comercial` (roles `comercial` y `gerente_comercial`) en toda la consola; `GerenteComercial` además en `/web/admin/*`. Los casos de uso sensibles revalidan el rol.
- `/pdfs/{archivo}` exige siempre Bearer. El enlace firmado `?t=` (ADR-011 §3), que solo emitía Google Chat, se retiró en HU-013.9 (#105): el parámetro se ignora.
- `/chat/events` (webhook de Google Chat con JWT de Google) se retiró en HU-013.9 (#105): responde 404.
- `/health` y `/health/ready` son anónimos.

## 7.3 Endpoints

El contrato congelado es `specs/openapi.yaml` (#17), fuente de verdad (SDD, ADR-010). Hoy está implementado:

| Método | Ruta | Respuesta |
|---|---|---|
| GET | `/health` | `200 {"status":"ok","servicio":"cotifly-api","entorno":"<ASPNETCORE_ENVIRONMENT>"}` |
| GET | `/health/ready` | `200 {"status":"ready"}` si `dbo_cotifly` acepta conexiones y tiene el 0002 aplicado; `503 {"status":"unavailable","detalle":"..."}` si no (sin cadena configurada, base caída, `esquema incompleto: falta 0002`, `falta 0004`, `falta 0005` o `falta 0006`) |
| POST | `/api/auth/login-clientepro` | `200 LoginResponse` (`access`, `refresh`, `user{id, username, email, nombre_completo, rol, ubicacion}`, `debeCambiarPassword=false`); `400 ErrorWeb` (cuerpo inválido); `401 {"error":"Credenciales inválidas"}` (cualquier rechazo); `429 ErrorWeb` |
| POST | `/api/auth/refresh` | `200 LoginResponse` con el par rotado; `401 {"error":"Inicia sesión de nuevo."}` (incluye cuerpo inválido, reuso y rotación perdida); `429` |
| POST | `/api/auth/logout` | Siempre `204` (con o sin `Authorization: Bearer`, con o sin `{"refresh": ...}`); `429` |
| GET | `/web/mis-cotizaciones` | `200 MisCotizaciones` `{total, cotizaciones: [CotizacionResumen]}`: solo las del comercial, máx. 100, de la más reciente a la más antigua |
| POST | `/web/cotizaciones/{folio}/enviar` | `200 RespuestaEnvio`; `400 ErrorWeb` (`CORREO_INVALIDO`) o `ErrorNegocio` (duplicado, sin PDF, relay); `404` si no es suya; `503 ErrorNegocio` `CORREO_DESHABILITADO` |
| POST | `/web/cotizaciones/{folio}/correo/vista-previa` | `200 VistaPreviaCorreo` (cuerpo opcional); `404` |
| POST | `/web/cotizaciones/{folio}/seguimiento` | `200 CotizacionResumen`; `400` (motivo > 2000, cuerpo inválido); `404` |
| GET | `/web/cotizaciones/{id}/nueva-version` | `200 DatosNuevaVersion` (`folio_base`, `version_siguiente`...); `404` (también si `id` no es UUID) |
| GET | `/web/clientes?q=` | `200 ResultadoBusquedaClientes` `{exacta, candidatos: [{nit_cliente, nombre, origen_cliente, bp}]}` desde Nexo (HU-005.2; `candidatos: []` si no hubo coincidencias; `exacta=true` solo con un único candidato); `400 ErrorWeb` si falta `q`; `400 ErrorNegocio` `CLIENTE_BUSQUEDA_NO_DISPONIBLE` si Nexo no está configurado, no responde o el comercial no tiene `idUsuarioExterno` |
| GET | `/web/clientes/{nit}/destinatarios-previos` | `200 Destinatarios` del último envío del comercial a ese NIT (vacío si no hay); `400` si el NIT supera 20 caracteres |
| GET | `/web/municipios` | `200 [Municipio]` `{id, codigo_dane, municipio, departamento, pais}`, catálogo completo de Nexo sin paginar (HU-005.13); `400 ErrorNegocio` `MUNICIPIOS_NO_DISPONIBLE` |
| GET | `/web/catalogos-flete` | `200 CatalogosFlete` `{tipos_vehiculo, tipos_carroceria, clases_viaje}` (cada opción `{valor, etiqueta}`) desde los catálogos maestros de Nexo (`GET /api/catalogos/*`, con caché y copia de respaldo; HU-013.10); la carrocería en código; `503 ErrorNegocio` `CATALOGOS_FLETE_NO_DISPONIBLE` si no hay copia utilizable |
| POST | `/web/solicitudes-costeo` | Cuerpo `SolicitudCosteoWeb` (formulario ampliado: `tipo`, `nit_cliente`, `linea_negocio`, `origen`, `destino` obligatorios; vehículo, carrocería, tipología, clase de viaje y de contenedor con el código de Nexo; `posiciones` hasta 200). Crea la cotización (folio del servidor con el NIT) y la envía a Nexo (HU-005.5, ADR-015). `201 CotizacionResumen` **aunque Nexo falle**: `estado_costeo` `pendiente_costeo`/`autocosteo`, o `error_envio` con `motivo_estado_costeo`. HU-013.14 (ADR-018 §17): `autocosteo` sale con `precio_final` (el costo se lee en la misma petición); si esa lectura falla, `pendiente_costeo`. HU-013.10: los catálogos se validan por código antes de guardar, `notas` y `origen_municipio_id`/`destino_municipio_id` viajan a Nexo, las horas del encabezado también van por línea y un `deadline` sin zona es hora de Bogotá; los 400 de Nexo se traducen (`ENUM_INVALIDO`, `LINEA_INCOMPLETA`, `PESO_*` → `SOLICITUD_COSTEO_INVALIDA`; `CLIENTE_*` → `COSTEO_NEXO_RECHAZADA`, reintentable; `DWCUBOS_NO_CONFIGURADO` → `COSTEO_NEXO_NO_DISPONIBLE`, reintentable). `400 ErrorNegocio` `SOLICITUD_COSTEO_INVALIDA`, `MARGEN_LINEA_DESCONOCIDA` o `FOLIO_CONSECUTIVO_AGOTADO`; `503` `CATALOGOS_FLETE_NO_DISPONIBLE` o `SERVICIO_OCUPADO` (#125; nada queda guardado) |
| POST | `/web/cotizaciones/{folio}/costeo/reintentar` | Reenvía a Nexo la solicitud guardada, sin volver a llenar el formulario. `200 CotizacionResumen`; `404` si no es suya; `409 ErrorNegocio` `COSTEO_NO_REINTENTABLE` si ya tiene resultado o no está en `error_envio` (o en un `pendiente_envio` de más de 2 minutos) |
| POST | `/web/cotizaciones/{folio}/costeo/re-solicitar` | HU-013.4 (#100, ADR-018 §5). "Cotizar costo": cuerpo `ReSolicitudCosteoWeb` `{motivo, datos_carga?, nueva_version?}`, `If-Match` obligatorio e `Idempotency-Key` opcional (la repetición devuelve la respuesta guardada). Nexo no tiene "reabrir": es una captura nueva con lo guardado. Mismo folio (desde `costeo_vencido`, `entregada` o `error_envio`, sin envío al cliente ni resultado): `200 CotizacionResumen` en `pendiente_costeo` (o `error_envio`), sin el costo y el precio anteriores (quedan en la auditoría `costeo_nexo_re_solicitud`), PDF invalidado y polling reanudado. Con `datos_carga` o `nueva_version`: `201` con la versión `-vN` y `Location`; la original no cambia. `400 SOLICITUD_COSTEO_INVALIDA`; `404`; `409 ESTADO_NO_PERMITE`, `PDF_YA_ENVIADO` o `CONFLICTO_CONCURRENCIA`; `428 PRECONDICION_REQUERIDA`. `reintentar` se conserva por compatibilidad |
| POST | `/web/cotizaciones/{folio}/resultado` | Cuerpo `ResultadoWeb` `{resultado: ganada\|perdida, motivo?, comentario?}` (HU-005.14). Primero Nexo (`POST /api/cotizaciones/{id}/resultado`) y, solo con 200, `ganada`, `motivo_rechazo` y `marcado_at` en Cotifly; `comentario` solo se reenvía. `200 CotizacionResumen`; `400`; `404` si no es suya; `409 ErrorNegocio` `RESULTADO_YA_DEFINIDO`, `COTIZACION_NO_ENTREGADA` (de Nexo) o `COSTEO_NO_LISTO` (no pasó por Nexo); `503` `COSTEO_NEXO_NO_DISPONIBLE`/`COSTEO_NEXO_TIEMPO_AGOTADO`. Sin cambio local en 409 y 503. `/seguimiento` queda obsoleto (compatibilidad): responde 409 `USAR_RESULTADO_NEXO` en el flujo de costeo (`estado_costeo` con valor) salvo `descartada` y `costeo_vencido`, que se marcan solo en Cotifly. `resultado` también: `COTIZACION_NO_ENTREGADA` sin llamar a Nexo en `pendiente_envio` (en `pendiente_costeo` primero lee el costeo de Nexo y solo sigue si ya tiene costo); si la cotización no tiene precio, relee el costeo tras el 200 de Nexo y guarda resultado y precio en la misma escritura (HU-013.14, ADR-018 §17), 409 `SOLICITUD_NO_TRANSICIONABLE`, 404 si Nexo no encuentra la cotización, 503 si Nexo responde 401/403 (configuración), 400 `COSTEO_NEXO_RECHAZADA` para otro 4xx |
| GET | `/web/cotizaciones/{folio}/costeo` | `200 CosteoCotizacion` `{folio, estado_costeo, motivo_estado_costeo, costeo_listo_at, precio_final, margen_aplicado, moneda, servicios_conexos: [{id, nombre, categoria, cantidad, precio, incluido}], pdf_url}`: solo precios con margen, nunca el costo de Nexo (HU-005.7); `404` si no es suya |
| PUT | `/web/cotizaciones/{folio}/costeo/margen` | HU-013.2 (#98, ADR-018 §3). Cuerpo `{margen, justificacion?}` e `If-Match` obligatorio. `200 CosteoCotizacion` con el precio del flete y de los conexos recalculado (`IClsPrecioService`, moneda y TRM fijadas), `origen_margen = negociado` y el `ETag` nuevo; el ajuste queda en `MargenAjustes` (sugerido, nuevo, usuario y justificación) y en `EventosHerramienta` (`ajustar_margen_costeo`); con PDF sin enviar lo invalida (`pdf_desactualizado`). El mismo margen vigente responde 200 sin registrar nada. `400` `AJUSTE_MARGEN_INVALIDO` (fuera de 0 a 0,99) o `JUSTIFICACION_REQUERIDA` (por debajo del sugerido; `detalle.margen_sugerido`); `404`; `409` `COSTEO_NO_LISTO`, `PDF_YA_ENVIADO` (con la llamada para crear `-vN`) o `CONFLICTO_CONCURRENCIA`; `428 PRECONDICION_REQUERIDA` sin `If-Match` |
| GET | `/web/servicios-conexos` | HU-013.3 (#99, ADR-018 §4). `?q=&categoria=&page=&page_size=` (hasta 100 y 20 caracteres, página desde 1, de 1 a 50 por página). Consulta `GET {Nexo}/api/servicios-conexos?soloActivos=true` con la identidad del comercial. `200 PaginaServiciosConexos` sin `costoUnitario` ni ningún costo; `400 ErrorWeb`; `403 NEXO_SIN_PERMISO`; `503 NEXO_NO_DISPONIBLE` |
| POST | `/web/cotizaciones/{folio}/costeo/servicios-conexos` | HU-013.3 (#99). Cuerpo `{items: [{linea_orden, servicio_id, cantidad}]}` (1 a 20), `If-Match` obligatorio e `Idempotency-Key` opcional (`IClsAlmacenIdempotencia`, ámbito `conexos:{usuarioId}`: la misma clave devuelve la respuesta guardada sin repetir el POST de Nexo; se guardan los éxitos y las respuestas con `agregado_en_nexo`). La versión se reserva en la base antes del POST: de varias peticiones simultáneas solo una llega a Nexo. Traduce `linea_orden` al `lineaId` de la cotización de Nexo (leída con `GET /api/cotizaciones/{id}` justo antes), llama a `POST /api/cotizaciones/{id}/conexos` sin `valorAplicado`, relee la cotización y aplica el margen con `clsPrecioMonedaCosteo`; auditado en `EventosHerramienta` (`agregar_servicios_conexos`). `200 CosteoCotizacion` con el `ETag` nuevo; `400` `ErrorWeb` o `CONEXO_INVALIDO`; `403 NEXO_SIN_PERMISO`; `404`; `409` `COSTEO_NO_LISTO`, `LINEA_SIN_COTIZACION_NEXO`, `PDF_YA_ENVIADO`, `ESTADO_NO_PERMITE` (`detalle.motivo = LICITACION`) o `CONFLICTO_CONCURRENCIA`; `428`; `503 NEXO_NO_DISPONIBLE` (con `detalle.agregado_en_nexo = true` si solo falló la relectura: el polling lo recoge) |
| PUT | `/web/cotizaciones/{folio}/costeo/servicios-conexos` | Cuerpo `{incluidos: [id]}` e `If-Match` obligatorio (#98). `200 CosteoCotizacion` con el `ETag` nuevo; con PDF sin enviar lo invalida (`pdf_desactualizado`, el anterior queda en `CotizacionPdfsHistorial`); la misma selección no cambia nada; `400` id desconocido; `404`; `409 ErrorNegocio` `COSTEO_NO_LISTO`, `PDF_YA_ENVIADO` o `CONFLICTO_CONCURRENCIA`; `428` sin `If-Match` |
| POST | `/web/cotizaciones/{folio}/costeo/textos/redactar` | HU-013.5 (#101, ADR-018 §2a). Sin cuerpo; `?sobrescribir=true` opcional e `If-Match` obligatorio. La IA (`IClsRedactorCotizacion` sobre `IChatClient`) recibe solo cliente, línea, rutas, vehículo, nombres de los conexos incluidos, moneda y comercial; su salida JSON se valida (sin montos, URL ni correos; 120/900 caracteres) y, si tarda más de `IA:TiempoRedaccionSegundos` (15), falla o no valida, se usan los de `clsTextosEstandar`. `200 TextosCotizacion` (`fuente` `ia` o `estandar`) con el `ETag` nuevo; se audita en `EventosHerramienta` (`redactar_textos_costeo`: proveedor, modelo, duración, respaldo). `404`; `409` `TEXTOS_EDITADOS` (sin llamar a la IA), `PDF_YA_ENVIADO` o `CONFLICTO_CONCURRENCIA`; `428` sin `If-Match`. Con PDF sin enviar lo invalida |
| PUT | `/web/cotizaciones/{folio}/costeo/textos` | HU-013.5 (#101). Cuerpo `{asunto, introduccion}` e `If-Match` obligatorio. `200 TextosCotizacion` con `editado = true` y el `ETag` nuevo (`editar_textos_costeo` en la auditoría, sin los textos). `400 TEXTOS_COTIZACION_INVALIDOS` (vacíos, más de 120/900 caracteres, URL o correos; los montos se permiten); `404`; `409` `PDF_YA_ENVIADO` o `CONFLICTO_CONCURRENCIA`; `428`. Con PDF sin enviar lo invalida. `POST .../costeo/pdf` imprime estos textos (o los estándar) y nunca llama a la IA |
| POST | `/web/cotizaciones/{folio}/costeo/pdf` | `201 SalidaPdf`: PDF con el flete a `precio_final` y solo los conexos incluidos; en una licitación (aunque tenga una línea) o con dos o más líneas (HU-011.5), layout de pliego con el resumen de todas las rutas y el total sin conexos (solo el resumen con más de 60 rutas o 20 orígenes, hasta 1000; `PDF_PAYLOAD_INVALIDO` en USD); `ETag` nuevo; con `pdf_desactualizado` regenera en el mismo folio y archiva el anterior como `{folio}-hN.pdf`; `If-Match` opcional; `404`; `409` `COSTEO_NO_LISTO`, `PDF_YA_GENERADO` (PDF vigente), `PDF_YA_ENVIADO` o `CONFLICTO_CONCURRENCIA`; `400` errores del PDF |
| POST | `/web/licitaciones/analizar-excel` | HU-011.2 (ADR-017). Multipart `archivo` (`.xlsx`/`.xlsm`, máx. 10 MB) reenviado a `POST /api/captura-solicitudes/analizar-excel` de Nexo; no guarda nada. `200 AnalisisExcel`; `400` `ARCHIVO_REQUERIDO`, `FORMATO_NO_SOPORTADO`, `ARCHIVO_DEMASIADO_GRANDE`, `EXCEL_INVALIDO`, `EXCEL_SIN_DATOS`; `503` Nexo no disponible |
| POST | `/web/licitaciones` | HU-011.2. Multipart (`licitacion` JSON + `archivo` opcional) o JSON `LicitacionWeb`. Valida todas las filas antes de guardar: `400 ReporteLicitacionInvalida` (`LICITACION_INVALIDA` + `errores[]` con `indice`, `fila`, `campo`, `codigo`); si son válidas crea la cotización `licitacion` con N líneas y el Excel original, la confirma y la envía a Nexo como una solicitud con N líneas: `201 Licitacion` aunque Nexo falle (`error_envio`, se reintenta con `.../costeo/reintentar`) |
| GET | `/web/licitaciones/{folio}` | `200 Licitacion` (estado y precio por línea, nunca el costo de Nexo); `404` si no es suya |
| GET | `/web/licitaciones/{folio}/excel` | HU-011.3. `200` xlsx (`{folio}.xlsx`): el Excel original con `Precio final Cotifly ({moneda})`, `Estado costeo Cotifly` y `Servicios conexos Cotifly` a la derecha (columnas del cliente intactas, `SIN COSTEO` en amarillo) y la hoja `Resumen Cotifly`; sin original, una hoja con los campos mapeados. Nunca costos de Nexo. `404` si no es suya o no tiene líneas; `400` `EXCEL_INVALIDO`/`EXCEL_SIN_DATOS` |
| POST | `/web/cubicacion` | "Calcular cubicaje" contra el cubicador (HU-023.2, #157, ADR-020). Cuerpo `CubicacionRequest` `{items: [{codigo_mercancia?, descripcion?, cantidad, largo_cm, ancho_cm, alto_cm, peso_kg?}], clase_viaje?}` (centímetros y kilogramos; sin código, la descripción se resuelve al tipo más parecido del catálogo). `200 ResultadoCubicacion` `{resultado, motivo_sin_seleccion, condicionada, vehiculos[], mercancias_condicionadas, advertencias, extradimension, mercancias[]}` de una corrida `PRUEBA-<guid>` que no se guarda; `400 ErrorNegocio` `ITEMS_REQUERIDOS`, `MERCANCIA_DESCONOCIDA` (también un ítem sin código ni descripción) y los rechazos del cubicador; `503` `CUBICADOR_NO_DISPONIBLE` (sin cubicador o caído, sin respaldo), `CUBICADOR_NO_AUTORIZADO` o `CUBICADOR_RESPUESTA_INVALIDA` |
| GET / PUT | `/web/usuario/telefono` | `200 TelefonoComercial`; PUT `400` fuera del patrón; `401` si el comercial ya no existe |
| GET | `/web/admin/lineas-negocio` | `200 [LineaNegocio]` (todas); `403` salvo `gerente_comercial` |
| PUT | `/web/admin/lineas-negocio/{id}` | `200 LineaNegocio` (auditado); `400` (margen fuera de 0–0,99, método, campo faltante, número como texto); `403`; `404` |
| GET | `/web/metricas/comercial?desde&hasta` | `200 MetricasComercial` `{periodo, usuario_id, indicadores, serie_mensual}` de las cotizaciones propias (comercial o gerente); `400 ErrorWeb` fechas no `AAAA-MM-DD`, `hasta < desde` o más de 3 años. Una fila por cadena de versiones: valores de la vigente, periodo por la primera creación y resultado más reciente de la cadena. Definiciones en el tag `metricas` del contrato y en [ADR-016](../specs/decisiones/ADR-016-metricas-agregadas-y-regional.md) |
| GET | `/web/metricas/regional?desde&hasta&usuario_id` | Solo `gerente_comercial`. `200 MetricasRegional` `{periodo, regional, fuente_regional, usuario_id, indicadores, lineas_negocio_activas, comerciales, serie_mensual, concentracion_ciudades}` de su regional (o de un comercial de ella; el gerente aparece en `comerciales` con su `rol` y sus cotizaciones cuentan en el agregado); `400` fechas o `usuario_id` no UUID; `403` comercial; `404 ErrorWeb` `Comercial no encontrado en tu regional.`; `409 ErrorNegocio` `REGIONAL_NO_ASIGNADA`; `503 ErrorNegocio` `REGIONAL_NO_DISPONIBLE` |
| GET | `/web/metricas/actividad` | **Borrador obsoleto.** `200 [ActividadComercial]`: el comercial su fila; el gerente su regional |
| GET | `/web/metricas/ajustes-margen` | **Borrador obsoleto.** `200 [AjustesMargenComercial]`, mismo alcance |
| GET | `/pdfs/{archivo}` | `200 application/pdf` inline (dueño o gerente de su regional, con Bearer); `401` (sin sesión); `404 ErrorWeb` (no existe o no es tuyo) |
| POST | `/web/chat` | Asistente IA por folio (HU-013.6, #102, ADR-018 §2b). Cuerpo `{sesion_id, folio, mensaje}`. `200 RespuestaChat` `{texto, herramientas, errores, accion_propuesta, precio, margen, pdf}`: `accion_propuesta` (opcional) es la acción que armó el servidor (`tipo` `ajustar_margen` \| `re_solicitar_costeo` \| `agregar_servicios_conexos`, `metodo`, `endpoint`, `cuerpo`, `resumen`, `row_version`) y el frontend la ejecuta solo si el comercial confirma; `errores` (HU-012.4): `[{herramienta, codigo}]` con los errores de negocio de las herramientas del turno; `precio`, `margen` y `pdf` están obsoletos y siempre van en `null`. `200 RespuestaChatError` `{texto, error: true, codigo}` ante un fallo del asistente o del proveedor (`codigo`: `IA_NO_DISPONIBLE` si fallaron todos los modelos, `LIMITE_MODELO` con el texto del límite de peticiones si es un 429, `TURNO_EN_CURSO`, `MAX_ITERACIONES`, `RESPUESTA_NO_ESTRUCTURADA` o `ERROR_INTERNO`; HU-013.16); `400` (`sesion_id inválido`, `folio inválido`, mensaje vacío o de más de 8000, o `ErrorNegocio` `FOLIO_REQUERIDO` sin folio); `401`; `404` (el folio no es del comercial, o `{"error":"Sesión no encontrada."}` si la sesión es de otro comercial); `429` (`LimiteChat`). `cotizacion_origen_id` y `moneda` se aceptan y se ignoran: eran del agente cotizador legado, retirado en HU-013.9 (#105) |

Los endpoints de Nexo (`/web/clientes?q=`, `/web/municipios`, `/web/catalogos-flete`, `/web/cubicacion` y los del costeo) usan la política `Comercial` como el resto de la consola. El backend reenvía la consulta a `Nexo__UrlBase` sin token: identifica al comercial ante Nexo con `X-Portal-clsUsuario-Id` (su `UsuariosWeb.idUsuarioExterno`, resuelto a partir del `sub` del JWT, nunca del cliente). Sin `Nexo__UrlBase` los cuatro responden su código `*_NO_DISPONIBLE`. `GET /web/municipios` sirve la foto del catálogo que guarda `Nexo__MinutosCacheMunicipios` (60 minutos por defecto, HU-005.13); el contrato no cambia. Detalle en [2.14](02-arquitectura.md#214-integración-con-nexo) y [3.5](03-infraestructura.md#35-servicios-externos).

Endpoints protegidos (parte B de #12): `Authorization: Bearer <access>`; sin token, vencido, de otra clave o revocado → `401 {"error":"Inicia sesión de nuevo."}`; rol insuficiente → `403 {"error":"No tienes permiso para esta operación."}`.

`/health/ready` ejecuta los health checks con etiqueta `ready` (hoy `clsBaseDatosHealthCheck`: `CanConnectAsync` sobre `clsCotiflyDbContext` más verificación de `Sesiones.estadoAgente` y `TokensRefresco`, con un límite de 5 s para toda la sonda). `detalle` nunca incluye la cadena ni el servidor; la excepción queda en el log. Ninguno de los dos exige autenticación. El `HEALTHCHECK` de la imagen (#27) consulta `/health` (vida), no `/health/ready`: una base caída no debe hacer que Swarm reinicie el contenedor.

Todas las operaciones del contrato están implementadas (`clsContratoRutasTests.PendientesParteC` quedó vacía con la parte C de #12). Todos los endpoints de `/web/*` y `/pdfs` sin token responden `401 {"error":"Inicia sesión de nuevo."}` (prueba que recorre el contrato).

## 7.4 Errores

Dos formas de error, ambas del contrato:

```json
{ "error": "Cotización no encontrada o no es tuya." }
```

```json
{ "error": { "codigo": "CORREO_DUPLICADO", "mensaje": "...", "detalle": { "motivo": "..." } } }
```

La primera (`ErrorWeb`) es la de autenticación, validación y acceso; la segunda (`ErrorNegocio`) la de los códigos de negocio de los casos de uso y de las herramientas del asistente. Los códigos válidos son el enum de `ErrorNegocio` en `openapi.yaml`; `Cotifly.Api.Tests` verifica que cada `clsCodigosError` exista allí.

Traducción de los códigos de los casos de uso a HTTP en `/web/*` (`Comun/clsRespuestasWeb`, una sola tabla para todos los controllers):

Errores de `/web/*` (`clsRespuestasWeb`, cumple la tabla "Para #12" de abajo):

| Código del caso de uso | HTTP |
|---|---|
| `COTIZACION_NO_ENCONTRADA`, `NO_ENCONTRADO` (no existe o no es tuya; PDF ajeno) | 404 `ErrorWeb` (`Cotización no encontrada o no es tuya.`, igual en ambos casos) |
| `NO_AUTENTICADO`, `USUARIO_NO_ENCONTRADO` | 401 `ErrorWeb` (`Inicia sesión de nuevo.`) |
| `SIN_PERMISO` (rol revalidado en el caso de uso) | 403 `ErrorWeb` |
| `CORREO_DESHABILITADO`, `REGIONAL_NO_DISPONIBLE`, `CATALOGOS_FLETE_NO_DISPONIBLE` (HU-013.10), `SERVICIO_OCUPADO` (#125: el candado del folio siguió ocupado tras los reintentos de `CandadoSql`; nada se guardó, reintentable) | 503 `ErrorNegocio` (`SERVICIO_OCUPADO` con `Retry-After: 1`) |
| `REGIONAL_NO_ASIGNADA` (tablero del gerente sin regional, #45) | 409 `ErrorNegocio` |
| `SOLICITUD_INVALIDA`, `TELEFONO_INVALIDO`, `CORREO_INVALIDO`, `SEGUIMIENTO_INVALIDO`, cuerpo mal formado | 400 `ErrorWeb` |
| `TRM_NO_DISPONIBLE` (HU-012: ninguna fuente de la TRM respondió, DolarApi ni la Superintendencia; no se cotiza en USD; en `POST /web/licitaciones` con `moneda = USD` es 400 y no se guarda nada) | 400 `ErrorNegocio` en los endpoints que convierten a USD (fila "Resto"). Hasta HU-013.9 (#105) lo devolvían también las herramientas `calcular_precio` y `registrar_ajuste_margen` del agente legado, en `errores` de `RespuestaChat` |
| `TARIFA_CONFIG_INVALIDA` (la fuente de costeo devolvió una tarifa en una moneda distinta de COP) | Lo devolvía `buscar_tarifa` del agente legado, retirado en HU-013.9; hoy ningún endpoint lo produce |
| Resto de códigos de negocio (`CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO`, `CORREO_ENVIO_FALLIDO`, `CONFIRMACION_REQUERIDA`, y los de Nexo: `CLIENTE_BUSQUEDA_NO_DISPONIBLE`, `MUNICIPIOS_NO_DISPONIBLE`, `ITEMS_REQUERIDOS`, `SOLICITUD_COSTEO_INVALIDA`...) | 400 `ErrorNegocio` (`detalle` como objeto `{"motivo": ...}`) |
| `COSTEO_NO_REINTENTABLE` | 409 `ErrorNegocio` |
| Cuerpo de más de `SeguridadHttp:LimiteCuerpoWebBytes` | 413 `ErrorWeb` (fuera del contrato: transporte) |

La validación automática de `[ApiController]` responde `400 ErrorWeb` (no `ProblemDetails`). `/api/auth/*`: 400 `ErrorWeb` por cuerpo inválido en el login, 401 genérico para cualquier rechazo, refresh con cuerpo inválido también 401, logout siempre 204, 429 al pasar el límite.

Tabla de origen, definida en #11 para los endpoints de #12 y cumplida por `clsRespuestasWeb` (la citan las pruebas de `clsRespuestasWebTests` y `clsWebEndpointsTests`):

**Para #12 (endpoints y agente):**

| Caso | Respuesta HTTP en `/web/*` |
|---|---|
| `COTIZACION_NO_ENCONTRADA` (no existe o no es suya) | 404 `ErrorWeb` (`NoEsTuya`) |
| `CORREO_DESHABILITADO` | 503 |
| `USUARIO_NO_ENCONTRADO` (teléfono: el comercial del token ya no existe) | 401 `ErrorWeb` |
| `TELEFONO_INVALIDO`, `CORREO_INVALIDO`, `SEGUIMIENTO_INVALIDO` | 400 `ErrorWeb` |
| Resto de códigos de envío (`CORREO_DUPLICADO`, `CORREO_PDF_NO_ENCONTRADO`, `CORREO_ENVIO_FALLIDO`, `CONFIRMACION_REQUERIDA`) | 400 `ErrorNegocio` |


## 7.5 Ejemplos

Login:

```http
POST /api/auth/login-clientepro
Content-Type: application/json

{ "idUsuario": "APEREZ" }
```

```json
{
  "access": "eyJhbGciOiJIUzI1NiIs...",
  "refresh": "fI1FJv7TFUPtgck3URCDw-VCY...",
  "user": {
    "id": "968c9f61-b1f1-44a8-bb23-7a3d31d7d602",
    "username": "APEREZ",
    "email": "aperez@example.com",
    "nombre_completo": "Ana Pérez (dev)",
    "rol": "comercial",
    "ubicacion": null
  },
  "debeCambiarPassword": false
}
```

Turno del asistente por folio:

```http
POST /web/chat
Authorization: Bearer eyJhbGciOiJIUzI1NiIs...
Content-Type: application/json

{ "sesion_id": "consola-5f2c9a1b", "folio": "PCN-DG-25092026-001", "mensaje": "Déjalo en 18 %, el cliente firmó volumen anual" }
```

```json
{
  "texto": "Te propongo ajustar el margen al 18 %. Revisa el resumen y confirma.",
  "herramientas": ["proponer_ajuste_margen"],
  "precio": null,
  "margen": null,
  "pdf": null,
  "errores": [],
  "accion_propuesta": {
    "tipo": "ajustar_margen",
    "metodo": "PUT",
    "endpoint": "/web/cotizaciones/PCN-DG-25092026-001/costeo/margen",
    "cuerpo": { "margen": 0.18, "justificacion": "el cliente firmó volumen anual" },
    "resumen": "…",
    "row_version": "…"
  }
}
```

Sin `folio`: `400 ErrorNegocio` `FOLIO_REQUERIDO`. Ante un fallo del asistente o del proveedor: `200 {"texto": "...", "error": true}` (`RespuestaChatError`). `precio`, `margen` y `pdf` están obsoletos y siempre van en `null` (los llenaba el agente cotizador legado, retirado en HU-013.9). El folio tiene el formato `PCN-[{nit}-]{INICIALES}-DDMMAAAA-{NNN}[-vN]` (iniciales del comercial, fecha de Bogotá y consecutivo diario por iniciales; [ADR-011](../specs/decisiones/ADR-011-folio-e-identidad-google-chat.md)). Los folios sin iniciales de la primera implementación se aceptan como heredados.

## 7.6 Límites, CORS y encabezados

| Tema | Valor | Configuración |
|---|---|---|
| Límite de `/api/auth/*` | 30 peticiones por minuto por IP (429 `ErrorWeb`) | `LimiteAuth:*` |
| Límite de `POST /web/chat` | 20 peticiones por minuto por comercial (429) | `LimiteChat:*` |
| Cuerpo de `/web/*` | 64 KB (413 `ErrorWeb`) | `SeguridadHttp:LimiteCuerpoWebBytes` |
| Cuerpo de `/web/licitaciones*` (Excel + JSON de hasta 1000 líneas) | 12 MB (413 `ErrorWeb`) | `SeguridadHttp:LimiteCuerpoLicitacionBytes` |
| Mensaje de chat | 1 a 8000 caracteres; `sesion_id` `^[A-Za-z0-9_-]{8,64}$` | Contrato |
| CORS | Orígenes exactos; GET, POST y PUT; encabezados `Authorization` y `Content-Type`; sin credenciales; preflight cacheado 10 minutos | `Cors:OrigenesPermitidos` (obligatorio en Production) |
| Encabezados de seguridad | `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`; `Cache-Control: no-store` en `/web/*` y `/pdfs/*`; sin `Server` | Fijo; HSTS lo pone Traefik |

Los límites son por proceso: con N réplicas el límite efectivo es N veces mayor ([10-operacion](10-operacion.md#106-riesgos-y-limitaciones-conocidos)). Detalle de la configuración en [03-infraestructura](03-infraestructura.md#seguridad-http).

## 7.7 Verificación del contrato

- `Cotifly.Api.Tests` lee `openapi.yaml` y recorre el contrato: cada operación está expuesta, ningún `/web/*` queda fuera del contrato, cada una responde 401 sin token, y los cuerpos reales (incluidos los DTOs y los casos de error) cumplen su esquema.
- El CI corre `redocly lint specs/openapi.yaml` (falla con errores; hoy hay 4 warnings aceptados: servidor `localhost` de ejemplo y operaciones de salud sin respuesta 4XX).
- La comparación contra un documento OpenAPI generado por la propia API (pendiente de ADR-010 §1) no se implementó: el recorrido de rutas y la validación de esquemas cubren la misma deriva sin agregar un generador de OpenAPI a la imagen.
- Las pruebas manuales con Postman de los endpoints `/web/*` eran parte de HU-003.9 ([qa/paridad-python-dotnet.md](qa/paridad-python-dotnet.md)), cerrada por decisión del usuario (2026-10-13, HU-022.1): el Python se retiró del repositorio (tag `python-final`), así que ya no hay contra qué comparar.
