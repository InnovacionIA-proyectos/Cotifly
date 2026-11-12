namespace Cotifly.Dominio.Comun;

/// <summary>
/// Códigos de error de negocio que devuelven las herramientas del agente (esquema <c>ErrorNegocio</c>
/// de <c>specs/openapi.yaml</c>, mismos códigos que el Python). <c>Cotifly.Api.Tests</c> verifica que
/// cada constante exista en el enum del contrato. Cada módulo agrega aquí los suyos.
/// </summary>
public static class clsCodigosError
{
    /// <summary>La línea de negocio no tiene mapeo TrqType ni política de margen.</summary>
    public const string MargenLineaDesconocida = "MARGEN_LINEA_DESCONOCIDA";

    /// <summary>Entrada que no permite calcular un precio (costo ≤ 0, margen fuera de rango, método desconocido).</summary>
    public const string PrecioInvalido = "PRECIO_INVALIDO";

    /// <summary>
    /// La fuente oficial de la TRM (Superintendencia Financiera, datos abiertos, ADR-014) no respondió, respondió algo inválido
    /// o no tiene TRM vigente para la fecha: no se cotiza en USD (HU-012.1/HU-012.2). Código nuevo del backend .NET.
    /// </summary>
    public const string TrmNoDisponible = "TRM_NO_DISPONIBLE";

    /// <summary>
    /// El margen negociado está fuera de rango para su método (negativo, o ≥ 1 con <c>margen</c>), el método no es
    /// <c>margen</c>/<c>markup</c> o falta el motivo. Código nuevo del backend .NET (agregado al contrato en #10).
    /// </summary>
    public const string AjusteMargenInvalido = "AJUSTE_MARGEN_INVALIDO";

    /// <summary>El ajuste de margen apunta a una cotización que no existe (o no es del comercial).</summary>
    public const string AjusteCotizacionInvalida = "AJUSTE_COTIZACION_INVALIDA";

    /// <summary>El payload del PDF no es válido (falta el folio, textos demasiado largos, sin filas de tarifa...).</summary>
    public const string PdfPayloadInvalido = "PDF_PAYLOAD_INVALIDO";

    /// <summary>El PDF no se pudo renderizar (fuentes o recursos embebidos no disponibles).</summary>
    public const string PdfRenderNoDisponible = "PDF_RENDER_NO_DISPONIBLE";

    /// <summary>
    /// Ya existe una cotización con ese folio. Código nuevo del backend .NET (#11): el Python versionaba solo el folio
    /// (<c>-v2</c>, <c>-v3</c>); en .NET el folio lo genera #12 (ADR-011) y la colisión se informa.
    /// </summary>
    public const string FolioDuplicado = "FOLIO_DUPLICADO";

    /// <summary>
    /// Ya se usaron los 999 folios del día de unas iniciales (<c>PCN-[nit-]INICIALES-DDMMAAAA-NNN</c>, ADR-011). Código nuevo del backend .NET
    /// (#12): el consecutivo diario por iniciales del comercial tiene tres dígitos.
    /// </summary>
    public const string FolioConsecutivoAgotado = "FOLIO_CONSECUTIVO_AGOTADO";

    /// <summary>El envío de correo exige la confirmación del comercial autenticado (<c>confirmado_por</c>).</summary>
    public const string ConfirmacionRequerida = "CONFIRMACION_REQUERIDA";

    /// <summary>La cotización no existe o no es del comercial (aislamiento por dueño).</summary>
    public const string CotizacionNoEncontrada = "COTIZACION_NO_ENCONTRADA";

    /// <summary>Ya se envió la cotización a los mismos destinatarios <c>para</c> hace menos de 5 minutos.</summary>
    public const string CorreoDuplicado = "CORREO_DUPLICADO";

    /// <summary>
    /// #228: el correo trae más destinatarios entre <c>para</c> y <c>cc</c> que el tope (<c>Correo:MaximoDestinatarios</c>, 10 por
    /// defecto): 400 <c>ErrorNegocio</c> con <c>detalle.maximo</c> y <c>detalle.recibidos</c>, en el envío y en la vista previa.
    /// </summary>
    public const string CorreoDemasiadosDestinatarios = "CORREO_DEMASIADOS_DESTINATARIOS";

    /// <summary>La cotización no tiene PDF en el almacén para adjuntar.</summary>
    public const string CorreoPdfNoEncontrado = "CORREO_PDF_NO_ENCONTRADO";

    /// <summary>El transporte de correo falló (red, relay que rechaza destinatarios, timeout).</summary>
    public const string CorreoEnvioFallido = "CORREO_ENVIO_FALLIDO";

    /// <summary>
    /// El envío de correo está deshabilitado (<c>Correo:Habilitado=false</c>, valor por defecto, igual que
    /// <c>correo_habilitado</c> del Python). Código nuevo del backend .NET (#11).
    /// </summary>
    public const string CorreoDeshabilitado = "CORREO_DESHABILITADO";

    /// <summary>
    /// HU-033 (#199): el correo no se puede reintentar en MailSender: no está <c>fallido</c>, nunca llegó a MailSender (se rechazó al
    /// encolarlo: hay que enviarlo de nuevo) o MailSender ya no lo conoce. 409.
    /// </summary>
    public const string CorreoNoReintentable = "CORREO_NO_REINTENTABLE";

    /// <summary>Fallo inesperado (ej. el comercial autenticado no existe en <c>UsuariosWeb</c>).</summary>
    public const string ErrorInterno = "ERROR_INTERNO";

    /// <summary>
    /// La búsqueda de clientes de Nexo (HU-005.2, ADR-005; <c>clsBuscadorClientesNexo</c>) no está disponible: <c>Nexo:UrlBase</c> vacía o
    /// Nexo no respondió. <c>GET /web/clientes</c> responde 503. Código nuevo del backend .NET (#12).
    /// </summary>
    public const string ClienteBusquedaNoDisponible = "CLIENTE_BUSQUEDA_NO_DISPONIBLE";

    /// <summary>El modelo pidió una herramienta que no existe (HU-003.10). Código nuevo del backend .NET (#12).</summary>
    public const string HerramientaDesconocida = "HERRAMIENTA_DESCONOCIDA";

    /// <summary>
    /// El catálogo de municipios (<c>GET /web/municipios</c>, endpoint de Nexo) no está disponible: Nexo sin
    /// configurar, sin respuesta o el comercial sin <c>idUsuarioExterno</c>. Código nuevo del backend .NET.
    /// </summary>
    public const string MunicipiosNoDisponible = "MUNICIPIOS_NO_DISPONIBLE";

    /// <summary>
    /// Los catálogos de flete (<c>GET /web/catalogos-flete</c>: tipos de vehículo, tipos de carrocería y clases de
    /// viaje, endpoint de Nexo) no están disponibles: mismas causas que <see cref="MunicipiosNoDisponible"/>. Código
    /// nuevo del backend .NET.
    /// </summary>
    public const string CatalogosFleteNoDisponible = "CATALOGOS_FLETE_NO_DISPONIBLE";

    /// <summary>
    /// <c>POST /web/cubicacion</c> sin <c>items</c> (falta o viene vacío). Nació con la cubicación rápida de Nexo; se conserva con
    /// "Calcular cubicaje" contra el cubicador (HU-023.2, #157). <c>CUBICACION_NO_DISPONIBLE</c> se retiró con esa cubicación (ahora
    /// son los del cubicador); <c>CUBICACION_DEMASIADO_GRANDE</c> volvió en HU-023 (#176) con el tope de 10.000 unidades del cubicador.
    /// Código nuevo del backend .NET.
    /// </summary>
    public const string ItemsRequeridos = "ITEMS_REQUERIDOS";

    /// <summary>
    /// El formulario ampliado del costeo (<c>POST /web/solicitudes-costeo</c>) no cumple los límites de Nexo (ADR-015). Mismo
    /// valor que <c>clsSolicitudCosteo.CodigoError</c>. Código nuevo del backend .NET.
    /// </summary>
    public const string SolicitudCosteoInvalida = "SOLICITUD_COSTEO_INVALIDA";

    /// <summary>
    /// El costeo de Nexo no está disponible: Nexo sin configurar, 5xx, red, circuito abierto o el comercial sin
    /// <c>idUsuarioExterno</c> (ADR-015). Código nuevo del backend .NET.
    /// </summary>
    public const string CosteoNexoNoDisponible = "COSTEO_NEXO_NO_DISPONIBLE";

    /// <summary>
    /// Nexo no respondió a tiempo. En un alta la solicitud pudo quedar creada (Nexo no ofrece clave de idempotencia, ADR-015).
    /// Código nuevo del backend .NET.
    /// </summary>
    public const string CosteoNexoTiempoAgotado = "COSTEO_NEXO_TIEMPO_AGOTADO";

    /// <summary>Nexo respondió 4xx; el código y el mensaje de Nexo van en el detalle (ADR-015). Código nuevo del backend .NET.</summary>
    public const string CosteoNexoRechazada = "COSTEO_NEXO_RECHAZADA";

    /// <summary>
    /// Nexo respondió 400 al alta (validación de los datos: <c>PESO_INCONSISTENTE</c>, <c>LINEA_INCOMPLETA</c>...): reenviar los mismos
    /// datos no puede funcionar, así que el <c>error_envio</c> es definitivo (ADR-015 §3). Código nuevo del backend .NET.
    /// </summary>
    public const string CosteoNexoSolicitudInvalida = "COSTEO_NEXO_SOLICITUD_INVALIDA";

    /// <summary>
    /// Nexo ya no reconoce la solicitud (404 <c>SOLICITUD_NO_ENCONTRADA</c> al consultarla): el polling la deja de consultar con el
    /// motivo, en vez de reintentar hasta la edad máxima (ADR-015 §4). Código nuevo del backend .NET.
    /// </summary>
    public const string CosteoNexoSolicitudNoEncontrada = "COSTEO_NEXO_SOLICITUD_NO_ENCONTRADA";

    /// <summary>
    /// Endurecimiento B (#199, E2E 2026-10-22): Nexo respondió 404 <c>COTIZACION_NO_ENCONTRADA</c> sobre la cotización de Nexo de un folio
    /// del comercial (por ejemplo, al marcar el resultado). Es 409, nunca el 404 "no es tuya": la cotización sí es del comercial en
    /// Cotifly y lo que falta es la de Nexo. El código de Nexo va en <c>detalle.motivo</c>. Código nuevo del backend .NET.
    /// </summary>
    public const string CotizacionNexoNoEncontrada = "COTIZACION_NEXO_NO_ENCONTRADA";

    /// <summary>
    /// <c>POST /web/cotizaciones/{folio}/costeo/reintentar</c> sobre una cotización que no está en <c>error_envio</c> ni en
    /// <c>pendiente_envio</c> (o no nació del formulario ampliado): 409. Código nuevo del backend .NET.
    /// </summary>
    public const string CosteoNoReintentable = "COSTEO_NO_REINTENTABLE";

    /// <summary>
    /// La cotización todavía no tiene el costeo de Nexo con margen aplicado (precio): no se pueden elegir servicios conexos ni
    /// generar el PDF del costeo (409, ADR-015 §5). Código nuevo del backend .NET.
    /// </summary>
    public const string CosteoNoListo = "COSTEO_NO_LISTO";

    /// <summary>La cotización ya tiene PDF: para cambiar precios o servicios se genera una versión nueva (409). Código nuevo del backend .NET.</summary>
    public const string PdfYaGenerado = "PDF_YA_GENERADO";

    /// <summary>
    /// Nexo rechazó cambiar el resultado de la cotización (ya estaba ganada o perdida con el otro valor): 409, sin cambio local
    /// (HU-005.14). Mismo código que usa Nexo.
    /// </summary>
    public const string ResultadoYaDefinido = "RESULTADO_YA_DEFINIDO";

    /// <summary>
    /// Nexo solo marca el resultado de una cotización entregada (su issue #171 quitó la "entrega implícita"): 409, sin cambio local.
    /// Mismo código que usa Nexo.
    /// </summary>
    public const string CotizacionNoEntregada = "COTIZACION_NO_ENTREGADA";

    /// <summary>
    /// <c>POST /web/cotizaciones/{folio}/seguimiento</c> sobre una cotización del flujo de costeo en Nexo (<c>estadoCosteo</c> con valor, salvo descartada o costeo vencido): el resultado
    /// se marca con <c>POST .../resultado</c>, que escribe primero en Nexo (409, HU-005.14). Código nuevo del backend .NET.
    /// </summary>
    public const string UsarResultadoNexo = "USAR_RESULTADO_NEXO";

    /// <summary>Nexo no puede pasar la solicitud a <c>Entregada</c> (está descartada): 409, sin cambio local. Mismo código que usa Nexo.</summary>
    public const string SolicitudNoTransicionable = "SOLICITUD_NO_TRANSICIONABLE";

    /// <summary>
    /// Licitaciones desde Excel (HU-011.2, ADR-017): falta el archivo o viene vacío. Mismo código que usa Nexo en
    /// <c>analizar-excel</c>.
    /// </summary>
    public const string ArchivoRequerido = "ARCHIVO_REQUERIDO";

    /// <summary>El archivo no es <c>.xlsx</c> ni <c>.xlsm</c>. Mismo código que usa Nexo.</summary>
    public const string FormatoNoSoportado = "FORMATO_NO_SOPORTADO";

    /// <summary>El archivo supera 10 MB (el límite de Nexo). Código nuevo del backend .NET.</summary>
    public const string ArchivoDemasiadoGrande = "ARCHIVO_DEMASIADO_GRANDE";

    /// <summary>Nexo (o Cotifly al releer el original) no pudo leer el Excel. Mismo código que usa Nexo.</summary>
    public const string ExcelInvalido = "EXCEL_INVALIDO";

    /// <summary>El Excel no tiene ninguna hoja con datos tabulares (o no tiene la hoja mapeada). Mismo código que usa Nexo.</summary>
    public const string ExcelSinDatos = "EXCEL_SIN_DATOS";

    /// <summary>
    /// <c>POST /web/licitaciones</c> con filas inválidas: 400 con el reporte por fila (<c>errores[]</c>: índice, fila, campo y
    /// código). Nada queda guardado. Código nuevo del backend .NET.
    /// </summary>
    public const string LicitacionInvalida = "LICITACION_INVALIDA";

    /// <summary>
    /// Nexo respondió 403 al alta de la solicitud o al análisis del Excel: el comercial no tiene el rol que pide Cabina de Fletes
    /// (<c>SIN_PERMISO_CABINA_FLETES</c>). Es configuración del usuario en Nexo, no una caída (ADR-017 §4). Código nuevo del backend .NET.
    /// </summary>
    public const string NexoSinPermiso = "NEXO_SIN_PERMISO";

    /// <summary>
    /// HU-013.2 (ADR-018 §3): el margen ajustado queda por debajo del margen sugerido (SAP TM o la política de la línea) y no trae
    /// justificación (400; <c>detalle.margen_sugerido</c>). Código nuevo del backend .NET.
    /// </summary>
    public const string JustificacionRequerida = "JUSTIFICACION_REQUERIDA";

    /// <summary>
    /// HU-013.5 (ADR-018 §2a): el asunto o la introducción del PDF que escribió el comercial están vacíos, son muy largos (120 y 900
    /// caracteres) o traen URL o correos (400). Código nuevo del backend .NET.
    /// </summary>
    public const string TextosCotizacionInvalidos = "TEXTOS_COTIZACION_INVALIDOS";

    /// <summary>
    /// HU-013.5: se pidió a la IA redactar sobre textos que el comercial ya editó; el frontend confirma con él y repite con
    /// <c>?sobrescribir=true</c> (409). Código nuevo del backend .NET.
    /// </summary>
    public const string TextosEditados = "TEXTOS_EDITADOS";

    /// <summary>
    /// Regla del PDF (ADR-018 §11): la cotización ya se envió al cliente o tiene resultado (HU-036.3, #207: o ya está finalizada); para
    /// cambiarla se crea la versión <c>-vN</c> (409; <c>detalle</c> trae la llamada a <c>re-solicitar</c> con <c>nueva_version</c>). Código
    /// nuevo del backend .NET.
    /// </summary>
    public const string PdfYaEnviado = "PDF_YA_ENVIADO";

    /// <summary>
    /// Concurrencia optimista (ADR-018 §8): el <c>If-Match</c> no coincide con la versión actual de la cotización
    /// (<c>Cotizaciones.versionNegocio</c>); nada cambió (409). Código nuevo del backend .NET.
    /// </summary>
    public const string ConflictoConcurrencia = "CONFLICTO_CONCURRENCIA";

    /// <summary>
    /// #125: otra operación retiene el candado del folio (<c>sp_getapplock</c>) más allá de los intentos configurados
    /// (<c>CandadoSql</c>); nada se guardó y se puede reintentar (503). Código nuevo del backend .NET.
    /// </summary>
    public const string ServicioOcupado = "SERVICIO_OCUPADO";

    /// <summary>Falta <c>If-Match</c> en una mutación de la etapa final del costeo (428, ADR-018 §8). Código nuevo del backend .NET.</summary>
    public const string PrecondicionRequerida = "PRECONDICION_REQUERIDA";

    /// <summary>
    /// HU-013.3 (ADR-018 §4): Nexo no responde a la consulta del catálogo de servicios conexos o a agregarlos (sin configurar, caído, sin
    /// respuesta a tiempo o el comercial sin <c>idUsuarioExterno</c>); 503. Código nuevo del backend .NET.
    /// </summary>
    public const string NexoNoDisponible = "NEXO_NO_DISPONIBLE";

    /// <summary>
    /// HU-013.3: el conexo no se puede agregar (la <c>linea_orden</c> no existe en la cotización, o Nexo respondió 400/404 al agregarlo,
    /// con su código en <c>detalle.motivo</c>); 400. Código nuevo del backend .NET.
    /// </summary>
    public const string ConexoInvalido = "CONEXO_INVALIDO";

    /// <summary>
    /// HU-013.3: la línea existe en Cotifly pero no tiene línea en la cotización de Nexo (sin flete, o sin el id de la línea de Nexo
    /// guardado); 409, sin llamar a Nexo. Código nuevo del backend .NET.
    /// </summary>
    public const string LineaSinCotizacionNexo = "LINEA_SIN_COTIZACION_NEXO";

    /// <summary>
    /// HU-021.8 (#135, decisión 6): la cotización tiene varias rutas y la petición (margen o conexos) no dice a cuál aplica; 400 con
    /// <c>detalle.rutas</c> (<c>linea_orden</c>, <c>linea_solicitud_id</c>, origen y destino de cada una, sin montos). Código nuevo del
    /// backend .NET.
    /// </summary>
    public const string RutaRequerida = "RUTA_REQUERIDA";

    /// <summary>
    /// HU-021.8 (#135): la ruta indicada (<c>linea_orden</c> o <c>linea_solicitud_id</c>) no es de la cotización, o las dos no son la
    /// misma ruta; 400, sin cambios. Código nuevo del backend .NET.
    /// </summary>
    public const string RutaInexistente = "RUTA_INEXISTENTE";

    /// <summary>
    /// HU-028.1 (#183, Nexo #320): la ruta indicada fue rechazada por la Mesa de Costeo de Nexo (todos sus camiones): no tiene precio, así
    /// que no admite ajuste de margen ni servicios conexos; 409 con <c>detalle.linea_orden</c>, <c>detalle.linea_solicitud_id</c> y
    /// <c>detalle.motivo_rechazo</c>. Código nuevo del backend .NET.
    /// </summary>
    public const string RutaRechazada = "RUTA_RECHAZADA";

    /// <summary>
    /// HU-028.1 (#183, decisión D1 de Nexo #320): la Mesa de Costeo rechazó TODAS las rutas de la cotización: no hay nada que cotizar y el
    /// PDF no se genera; 409. Código nuevo del backend .NET.
    /// </summary>
    public const string SinRutasCotizables = "SIN_RUTAS_COTIZABLES";

    /// <summary>
    /// HU-013 (ADR-018 §4 y §5): el estado o el tipo de la cotización no permite la acción (una licitación no admite conexos, con
    /// <c>detalle.motivo = LICITACION</c>; re-solicitar con un costeo en curso); 409. Código nuevo del backend .NET.
    /// </summary>
    public const string EstadoNoPermite = "ESTADO_NO_PERMITE";

    /// <summary>
    /// HU-028 (#186): la edición de la solicitud intenta cambiar un campo bloqueado (el cliente, el <c>tipo</c> spot/licitación o la
    /// línea de negocio de una ruta; o una ruta nueva con una línea que la solicitud no tiene); 400 con <c>detalle.campo</c> (el primero)
    /// y <c>detalle.campos</c> (todos). Nada cambia. Código nuevo del backend .NET.
    /// </summary>
    public const string CampoNoEditable = "CAMPO_NO_EDITABLE";

    /// <summary>
    /// HU-013.6 (#102, ADR-018 §2b): <c>POST /web/chat</c> sin <c>folio</c> (o vacío): el asistente solo trabaja sobre una cotización
    /// ya creada (la creación por chat se retiró, HU-013.9); 400. Código nuevo del backend .NET.
    /// </summary>
    public const string FolioRequerido = "FOLIO_REQUERIDO";

    /// <summary>HU-021.6: crear una cotización desde el chat está retirado (<c>Pdf:PermitirCotizacionChat = false</c>).</summary>
    public const string CotizacionChatRetirada = "COTIZACION_CHAT_RETIRADA";

    /// <summary>
    /// HU-021.5 (#133): el cubicador no pudo recomendar los vehículos (sin configurar, caído, sin respuesta a tiempo, respuesta inválida
    /// o rechazo) o su catálogo de tipos de mercancía no está disponible (<c>GET /web/tipos-mercancia</c>); 503. En la captura solo llega
    /// al cliente con <c>Cubicador:RespaldoSiFalla = false</c>: por defecto la ruta sigue con un camión sin vehículo. Código nuevo del
    /// backend .NET.
    /// </summary>
    public const string CubicadorNoDisponible = "CUBICADOR_NO_DISPONIBLE";

    /// <summary>
    /// Opción C (E2E 2026-10-16, D3): todas las llamadas permitidas a <c>POST /recomendaciones</c> (<c>Cubicador:MaximoConcurrente</c>, 1 por
    /// defecto) siguen en curso y no se liberó ninguna dentro de <c>Cubicador:EsperaMaximaCupoSegundos</c>. Nada se pidió al cubicador; se
    /// puede reintentar: 503 con <c>Retry-After</c>. En la captura, el recálculo y la licitación cuenta como una caída (respaldo o
    /// <c>CUBICADOR_NO_DISPONIBLE</c>); en "Calcular cubicaje" llega tal cual.
    /// </summary>
    public const string CubicadorOcupado = "CUBICADOR_OCUPADO";

    /// <summary>
    /// HU-023.1 (#156): el cubicador respondió 200 pero la respuesta no cumple el contrato (por ejemplo <c>ASIGNADO</c> con un vehículo
    /// sin peso, volumen, porcentajes o códigos; un resultado o un motivo desconocido; un porcentaje fuera de 0 a 100). Se rechaza para no
    /// guardar datos incompletos (el fallo silencioso): no se guarda nada y nunca va al respaldo. 503.
    /// </summary>
    public const string CubicadorRespuestaInvalida = "CUBICADOR_RESPUESTA_INVALIDA";

    /// <summary>
    /// D1 (E2E 2026-10-16): el cubicador rechazó con 422 (validación de su esquema) los datos de <c>POST /recomendaciones</c> y el
    /// rechazo no señala una medida, un peso o una cantidad de una mercancía (esos llegan como <c>DIMENSIONES_INVALIDAS</c> o
    /// <c>POSICION_INVALIDA</c> con <c>detalle.item</c>). Es un rechazo de los datos, no una caída: 400, nunca va al respaldo.
    /// </summary>
    public const string CubicadorPeticionRechazada = "CUBICADOR_PETICION_RECHAZADA";

    /// <summary>
    /// HU-023.1 (#156): el cubicador rechazó la llave de Cotifly (401 <c>NO_AUTORIZADO</c> del contrato). Es un problema de configuración
    /// de Cotifly (<c>Cubicador:ApiKey</c>), no de la sesión del comercial: 503, nunca 401. Nunca va al respaldo (auditoría de HU-023): la
    /// captura falla aunque <c>Cubicador:RespaldoSiFalla</c> esté encendido.
    /// </summary>
    public const string CubicadorNoAutorizado = "CUBICADOR_NO_AUTORIZADO";

    /// <summary>HU-023.1 (#156): 400 del cubicador, la ruta no trae mercancías (<c>MERCANCIAS_REQUERIDAS</c>). 400.</summary>
    public const string MercanciasRequeridas = "MERCANCIAS_REQUERIDAS";

    /// <summary>HU-023.1 (#156): 400 del cubicador, un <c>codigoMercancia</c> no está en su catálogo (<c>MERCANCIA_DESCONOCIDA</c>). 400.</summary>
    public const string MercanciaDesconocida = "MERCANCIA_DESCONOCIDA";

    /// <summary>HU-023.1 (#156): 400 del cubicador, la mercancía no se puede cubicar (<c>MERCANCIA_NO_CUBICABLE</c>). 400.</summary>
    public const string MercanciaNoCubicable = "MERCANCIA_NO_CUBICABLE";

    /// <summary>HU-023.1 (#156): 400 del cubicador, medidas o peso inválidos (<c>DIMENSIONES_INVALIDAS</c>). 400.</summary>
    public const string DimensionesInvalidas = "DIMENSIONES_INVALIDAS";

    /// <summary>
    /// HU-023 (#176): la ruta pide más de <c>10.000</c> unidades en total (suma de <c>cantidad</c> de sus mercancías), el tope del
    /// cubicador por petición. Cotifly lo valida antes de llamar y traduce igual el 400 del cubicador. Se resuelve partiendo la carga. 400.
    /// </summary>
    public const string CubicacionDemasiadoGrande = "CUBICACION_DEMASIADO_GRANDE";

    /// <summary>
    /// #278 (contrato único de NEXO, DU §8): la línea de negocio que viajaría a NEXO (<c>MargenesPolitica.mapeoLineasNexo</c> de la línea de
    /// la ruta) no es una de las seis de NEXO. Cotifly lo valida antes de llamar (en la captura y la licitación: 400 y nada se guarda; al
    /// reenviar una guardada: <c>error_envio</c> reintentable) y traduce igual el <c>400 LINEA_NEGOCIO_INVALIDO</c> de NEXO. Lo corrige soporte en el
    /// mapeo (no hay endpoint para editarlo). 400; <c>detalle.linea_negocio</c>, <c>detalle.linea_negocio_nexo</c> y <c>detalle.permitidas</c>. Mismo
    /// código que usa NEXO.
    /// </summary>
    public const string LineaNegocioInvalido = "LINEA_NEGOCIO_INVALIDO";

    /// <summary>
    /// #278 (DU §2.1 y §9): NEXO respondió <c>429 IA_TOPE_DIARIO</c>: el comercial agotó su cupo DIARIO de IA en NEXO (análisis del Excel).
    /// No es el <c>429 DEMASIADAS_SOLICITUDES</c> del cupo por minuto (ese es <c>COSTEO_NEXO_NO_DISPONIBLE</c>, reintentable tras el
    /// <c>Retry-After</c>): no se reintenta hasta el día siguiente. 429. Mismo código que usa NEXO.
    /// </summary>
    public const string IaTopeDiario = "IA_TOPE_DIARIO";

    /// <summary>
    /// HU-036.2 (#206): <c>POST .../finalizar</c> sobre una cotización cuyo <c>estado_costeo</c> no es <c>autocosteo</c> ni <c>entregada</c>
    /// (sin el costeo de Nexo listo no hay precio que aceptar; el chat y las licitaciones sin costeo tampoco). 409; <c>detalle.estado_costeo</c>
    /// trae el estado actual. Código nuevo del backend .NET.
    /// </summary>
    public const string CotizacionNoFinalizable = "COTIZACION_NO_FINALIZABLE";

    /// <summary>
    /// HU-036.3 (#207): el PDF, el envío por correo, el resultado o los textos del PDF exigen la cotización finalizada ("Finaliza la
    /// cotización antes de..."). 409; <c>detalle</c> trae la llamada para finalizar (<c>accion = finalizar</c>, <c>metodo</c>,
    /// <c>endpoint</c>). No aplica a las ya entregadas al cliente (anteriores a HU-036) ni a las que no pasan por Nexo. Código nuevo del
    /// backend .NET.
    /// </summary>
    public const string CotizacionNoFinalizada = "COTIZACION_NO_FINALIZADA";

    /// <summary>
    /// HU-018.1 (#155, parte B): al marcar <c>perdida</c> (<c>POST .../resultado</c> o <c>POST .../seguimiento</c>) el motivo es obligatorio y
    /// debe ser uno de la lista configurada (<c>Resultado:MotivosPerdida</c>, la de NEXO, <c>GET /web/motivos-perdida</c>), y el motivo
    /// <c>Otro</c> exige <c>comentario</c>. 400; <c>detalle.motivos_permitidos</c> trae la lista y <c>detalle.requiere_comentario</c> si
    /// solo faltó el comentario. Código nuevo del backend .NET.
    /// </summary>
    public const string MotivoNoPermitido = "MOTIVO_NO_PERMITIDO";

    /// <summary>
    /// HU-042.2 (#243, ADR-027): <c>GET /web/admin/sap-margen/estado</c> o <c>POST /web/admin/sap-margen/sincronizar</c> en un ambiente sin
    /// la sincronización del margen de SAP TM configurada (falta <c>SapTm:Url</c>, <c>SapTm:User</c> o <c>SapTm:Pass</c>). El margen sigue
    /// con la copia local existente o, sin ella, con la política de la línea. 503. Código nuevo del backend .NET.
    /// </summary>
    public const string SapMargenNoConfigurado = "SAP_MARGEN_NO_CONFIGURADO";

    /// <summary>
    /// HU-042.2 (#243, ADR-027): <c>POST /web/admin/sap-margen/sincronizar</c> mientras esta réplica ya sincroniza el margen de SAP TM (el
    /// worker o otra petición). Nada cambia; se reintenta cuando termine. 409. Código nuevo del backend .NET.
    /// </summary>
    public const string SapMargenSincronizacionEnCurso = "SAP_MARGEN_SINCRONIZACION_EN_CURSO";

    /// <summary>
    /// HU-042.2 (#243): <c>/web/admin/sap-margen/*</c> con la base de Cotifly sin responder (no se pudo leer el estado de la copia o
    /// registrar la corrida). 503, reintentable. Código nuevo del backend .NET.
    /// </summary>
    public const string SapMargenNoDisponible = "SAP_MARGEN_NO_DISPONIBLE";

    /// <summary>
    /// HU-045.2 (#264, decisión 4 de HU-045): incluir el seguro de la mercancía en una ruta sin <c>valor_mercancia</c> (el seguro es
    /// <c>valor_mercancia × tasa</c>). 400 con <c>detalle.linea_orden</c> y <c>detalle.linea_solicitud_id</c>; nada cambia. Código nuevo del
    /// backend .NET. HU-045.3 (#265): también es el <c>codigo</c> del aviso
    /// <c>aviso_seguro</c> (costeo, detalle y respuesta del PDF) de las rutas con el seguro marcado sin valor de la mercancía (no bloquea).
    /// </summary>
    public const string SeguroSinValorMercancia = "SEGURO_SIN_VALOR_MERCANCIA";

    // HU-044.1 (#252, ADR-028): importación inteligente del Excel con NEXO. Los códigos que siguen son los 400/404 de NEXO
    // (CONTRATO.md §3.3 y §3.19.1) que el adaptador devuelve tal cual, con el mensaje de NEXO. Mismos códigos que usa NEXO.

    /// <summary>HU-044.1 (#252): <c>sugerir-mapeo</c> o <c>plantillas-mapeo</c> sin encabezados. 400. Mismo código que usa NEXO.</summary>
    public const string EncabezadosRequeridos = "ENCABEZADOS_REQUERIDOS";

    /// <summary>HU-044.1 (#252): más de 100 columnas en el mapeo. 400. Mismo código que usa NEXO.</summary>
    public const string DemasiadasColumnas = "DEMASIADAS_COLUMNAS";

    /// <summary>HU-044.1 (#252): más de 5000 filas en <c>aplicar-mapeo</c>. 400. Mismo código que usa NEXO.</summary>
    public const string DemasiadasFilas = "DEMASIADAS_FILAS";

    /// <summary>HU-044.1 (#252): un encabezado de la plantilla de mapeo pasa de 200 caracteres. 400. Mismo código que usa NEXO.</summary>
    public const string EncabezadoDemasiadoLargo = "ENCABEZADO_DEMASIADO_LARGO";

    /// <summary>HU-044.1 (#252): una columna de la plantilla apunta a un índice que no está entre los encabezados. 400. Mismo código que usa NEXO.</summary>
    public const string ColumnaFueraDeRango = "COLUMNA_FUERA_DE_RANGO";

    /// <summary>HU-044.1 (#252): una columna del mapeo con un índice inválido. 400. Mismo código que usa NEXO.</summary>
    public const string ColumnaInvalida = "COLUMNA_INVALIDA";

    /// <summary>HU-044.1 (#252): el mapeo confirmado viene vacío. 400. Mismo código que usa NEXO.</summary>
    public const string MapeoRequerido = "MAPEO_REQUERIDO";

    /// <summary>HU-044.1 (#252): un campo del mapeo (o de <c>normalizar-valores</c>) que NEXO no conoce. 400. Mismo código que usa NEXO.</summary>
    public const string CampoInvalido = "CAMPO_INVALIDO";

    /// <summary>HU-044.1 (#252): dos columnas del mapeo apuntan al mismo campo. 400. Mismo código que usa NEXO.</summary>
    public const string CampoDuplicado = "CAMPO_DUPLICADO";

    /// <summary>
    /// HU-044.1 (#252): al mapeo le falta un campo obligatorio (origen y destino, o una columna <c>dividir_origen_destino</c>). 400. Mismo
    /// código que usa NEXO (y el mismo valor que <c>clsCodigosLineaSolicitud.CampoRequerido</c> de los errores por campo).
    /// </summary>
    public const string CampoRequerido = "CAMPO_REQUERIDO";

    /// <summary>HU-044.1 (#252): una transformación que no está en la lista cerrada de NEXO. 400. Mismo código que usa NEXO.</summary>
    public const string TransformacionInvalida = "TRANSFORMACION_INVALIDA";

    /// <summary>HU-044.1 (#252): una transformación que no corresponde al campo (ej. <c>fecha</c> en <c>peso</c>). 400. Mismo código que usa NEXO.</summary>
    public const string TransformacionNoAplica = "TRANSFORMACION_NO_APLICA";

    /// <summary>HU-044.1 (#252): <c>normalizar-valores</c> sin valores. 400. Mismo código que usa NEXO.</summary>
    public const string ValoresRequeridos = "VALORES_REQUERIDOS";

    /// <summary>HU-044.1 (#252): más de 1000 valores en <c>normalizar-valores</c>. 400. Mismo código que usa NEXO.</summary>
    public const string DemasiadosValores = "DEMASIADOS_VALORES";

    /// <summary>HU-044.1 (#252): un valor de <c>normalizar-valores</c> pasa de 150 caracteres. 400. Mismo código que usa NEXO.</summary>
    public const string ValorDemasiadoLargo = "VALOR_DEMASIADO_LARGO";

    /// <summary>
    /// HU-044.1 (#252): falta el cliente. Lo responde NEXO al guardar una plantilla o una receta sin cliente y lo responde Cotifly, sin llamar a
    /// NEXO, al listar recetas o plantillas sin el NIT de la licitación (sin él, NEXO devolvería las de todos los clientes). 400. Mismo
    /// código que usa NEXO.
    /// </summary>
    public const string ClienteRequerido = "CLIENTE_REQUERIDO";

    /// <summary>HU-044.1 (#252): el <c>clienteId</c> enviado a NEXO no existe. 400. Mismo código que usa NEXO.</summary>
    public const string ClienteNoEncontrado = "CLIENTE_NO_ENCONTRADO";

    /// <summary>
    /// HU-044.1 (#252): al guardar una plantilla o una receta, el NIT no es de ningún cliente ni prospecto del DW (NEXO no puede crear el
    /// cliente local). 400. Mismo código que usa NEXO.
    /// </summary>
    public const string ClienteNitInvalido = "CLIENTE_NIT_INVALIDO";

    /// <summary>HU-044.1 (#252): el nombre de la plantilla o de la receta pasa de 100 caracteres. 400. Mismo código que usa NEXO.</summary>
    public const string NombreDemasiadoLargo = "NOMBRE_DEMASIADO_LARGO";

    /// <summary>HU-044.1 (#252): <c>DELETE plantillas-mapeo/{id}</c> de una plantilla que no existe (404 en NEXO). Mismo código que usa NEXO.</summary>
    public const string PlantillaNoEncontrada = "PLANTILLA_NO_ENCONTRADA";

    /// <summary>HU-044.1 (#252): las instrucciones de <c>leer-estructura</c> o de la receta pasan de 4000 caracteres. 400. Mismo código que usa NEXO.</summary>
    public const string InstruccionesDemasiadoLargas = "INSTRUCCIONES_DEMASIADO_LARGAS";

    /// <summary>HU-044.1 (#252): <c>PUT recetas-lectura</c> sin receta. 400. Mismo código que usa NEXO.</summary>
    public const string RecetaRequerida = "RECETA_REQUERIDA";

    /// <summary>
    /// HU-044.1 (#252): la receta de lectura no es un JSON válido, es demasiado grande o no se puede ejecutar sobre el archivo (el mensaje de
    /// NEXO dice qué). 400. Mismo código que usa NEXO.
    /// </summary>
    public const string RecetaInvalida = "RECETA_INVALIDA";

    /// <summary>HU-044.1 (#252): al guardarla, la receta no da ninguna fila con ese archivo. 400. Mismo código que usa NEXO.</summary>
    public const string RecetaSinFilas = "RECETA_SIN_FILAS";

    /// <summary>HU-044.1 (#252): <c>DELETE recetas-lectura/{id}</c> de un formato que no existe (404 en NEXO). Mismo código que usa NEXO.</summary>
    public const string RecetaNoEncontrada = "RECETA_NO_ENCONTRADA";

    /// <summary>HU-044.1 (#252): <c>GET diccionario-cliente</c> con un <c>tipo</c> distinto de columna, unidad o valor. 400. Mismo código que usa NEXO.</summary>
    public const string TipoTerminoInvalido = "TIPO_TERMINO_INVALIDO";

    /// <summary>
    /// HU-044.2 (#253, auditoría B7): en <c>leer-estructura</c> el archivo cabe solo, pero con la <c>receta</c> y las <c>instrucciones</c> el
    /// envío pasa el tope de 10 MB de NEXO (el mismo de <c>clsArchivoExcel.TamanoMaximoBytes</c>). Lo responde Cotifly, sin llamar a NEXO,
    /// para no culpar al archivo con un <c>ARCHIVO_DEMASIADO_GRANDE</c>. 400. Código nuevo del backend .NET.
    /// </summary>
    public const string LecturaDemasiadoGrande = "LECTURA_DEMASIADO_GRANDE";
}
