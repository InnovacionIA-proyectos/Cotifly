namespace Cotifly.Dominio.Comun;

/// <summary>
/// Códigos de error de negocio que devuelven las herramientas del agente (esquema <c>ErrorNegocio</c>
/// de <c>specs/openapi.yaml</c>, mismos códigos que el Python). <c>Cotifly.Api.Tests</c> verifica que
/// cada constante exista en el enum del contrato. Cada módulo agrega aquí los suyos.
/// </summary>
public static class clsCodigosError
{
    /// <summary>No hay fuente de costeo disponible (el tarifario de Drive se retiró; Nexo llega con HU-005).</summary>
    public const string TarifaFuenteNoDisponible = "TARIFA_FUENTE_NO_DISPONIBLE";

    /// <summary>La fuente de margen (SAP TM) no respondió o no está configurada.</summary>
    public const string MargenFuenteNoDisponible = "MARGEN_FUENTE_NO_DISPONIBLE";

    /// <summary>La línea de negocio no tiene mapeo TrqType ni política de margen.</summary>
    public const string MargenLineaDesconocida = "MARGEN_LINEA_DESCONOCIDA";

    /// <summary>Entrada que no permite calcular un precio (costo ≤ 0, margen fuera de rango, método desconocido).</summary>
    public const string PrecioInvalido = "PRECIO_INVALIDO";

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
    /// El comercial ya usó los 999 folios del día (<c>PCN-[nit-]DDMMAAAA-NNN</c>, ADR-011). Código nuevo del backend .NET
    /// (#12): el consecutivo diario por comercial tiene tres dígitos.
    /// </summary>
    public const string FolioConsecutivoAgotado = "FOLIO_CONSECUTIVO_AGOTADO";

    /// <summary>El envío de correo exige la confirmación del comercial autenticado (<c>confirmado_por</c>).</summary>
    public const string ConfirmacionRequerida = "CONFIRMACION_REQUERIDA";

    /// <summary>La cotización no existe o no es del comercial (aislamiento por dueño).</summary>
    public const string CotizacionNoEncontrada = "COTIZACION_NO_ENCONTRADA";

    /// <summary>Ya se envió la cotización a los mismos destinatarios <c>para</c> hace menos de 5 minutos.</summary>
    public const string CorreoDuplicado = "CORREO_DUPLICADO";

    /// <summary>La cotización no tiene PDF en el almacén para adjuntar.</summary>
    public const string CorreoPdfNoEncontrado = "CORREO_PDF_NO_ENCONTRADO";

    /// <summary>El transporte de correo falló (red, relay que rechaza destinatarios, timeout).</summary>
    public const string CorreoEnvioFallido = "CORREO_ENVIO_FALLIDO";

    /// <summary>
    /// El envío de correo está deshabilitado (<c>Correo:Habilitado=false</c>, valor por defecto, igual que
    /// <c>correo_habilitado</c> del Python). Código nuevo del backend .NET (#11).
    /// </summary>
    public const string CorreoDeshabilitado = "CORREO_DESHABILITADO";

    /// <summary>Fallo inesperado (ej. el comercial autenticado no existe en <c>UsuariosWeb</c>).</summary>
    public const string ErrorInterno = "ERROR_INTERNO";
}
