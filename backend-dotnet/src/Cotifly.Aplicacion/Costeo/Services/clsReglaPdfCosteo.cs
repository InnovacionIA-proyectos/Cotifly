using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.Services;

/// <summary>
/// Regla del PDF de la etapa final del costeo (ADR-018 §11), compartida por el ajuste de margen y la selección de conexos (#98) y
/// por los conexos en Nexo, la re-solicitud y los textos (#99, #100, #101):
/// <list type="bullet">
/// <item>ya entregada al cliente (enviada o con resultado): 409 <c>PDF_YA_ENVIADO</c> con la llamada para crear la versión
/// <c>-vN</c> en <c>detalle</c> (<see cref="Validar"/>);</item>
/// <item>si no, la acción se permite e invalida el PDF vigente, que queda en el historial (<see cref="InvalidarPdf"/>).</item>
/// </list>
/// </summary>
public static class clsReglaPdfCosteo
{
    /// <summary>Endpoint (relativo) que crea la versión nueva de la cotización.</summary>
    public static string EndpointReSolicitar(string folio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folio);
        return $"/web/cotizaciones/{folio}/costeo/re-solicitar";
    }

    /// <summary>
    /// <c>PDF_YA_ENVIADO</c> con su <c>detalle</c> (<c>accion = nueva_version</c>, <c>metodo</c>, <c>endpoint</c> y <c>cuerpo</c> con
    /// <paramref name="motivoVersion"/> y <c>nueva_version = true</c>) si la cotización ya se entregó al cliente; si no, <c>null</c>.
    /// </summary>
    public static clsErrorDominio? Validar(clsCotizacionSolicitud cotizacion, string motivoVersion)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentException.ThrowIfNullOrWhiteSpace(motivoVersion);
        return cotizacion.ValidarCambioEtapaFinal() is { } error ? ConVersionNueva(error, cotizacion.Folio, motivoVersion) : null;
    }

    /// <summary>Agrega a un <c>PDF_YA_ENVIADO</c> del dominio la llamada para crear la versión nueva (contrato, ejemplo del 409).</summary>
    public static clsErrorDominio ConVersionNueva(clsErrorDominio error, string folio, string motivoVersion)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error with
        {
            Datos = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["accion"] = "nueva_version",
                ["metodo"] = "POST",
                ["endpoint"] = EndpointReSolicitar(folio),
                ["cuerpo"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["motivo"] = motivoVersion, ["nueva_version"] = true },
            },
        };
    }

    /// <summary>
    /// Invalida el PDF vigente (<c>pdf_url = null</c>, <c>pdf_desactualizado = true</c>) y deja el anterior en el historial con la
    /// acción y el comercial. Devuelve <c>true</c> si había PDF. Se escribe al guardar la unidad de trabajo.
    /// </summary>
    public static bool InvalidarPdf(clsCotizacionSolicitud cotizacion, MotivoInvalidacionPdf motivo, Guid usuarioId, IClsCotizacionRepository cotizaciones)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        ArgumentNullException.ThrowIfNull(cotizaciones);
        if (cotizacion.InvalidarPdf() is not { } anterior)
        {
            return false;
        }

        cotizaciones.AgregarHistorialPdf(clsCotizacionPdfHistorial.Registrar(cotizacion.Id, anterior, motivo, usuarioId == Guid.Empty ? null : usuarioId));
        return true;
    }

    /// <summary><c>CONFLICTO_CONCURRENCIA</c> (409): el <c>If-Match</c> no es la versión actual de la cotización.</summary>
    public static clsErrorDominio Conflicto(string folio) => new(clsCodigosError.ConflictoConcurrencia,
        $"La cotización {folio} cambió después de que la consultaste: recárgala y vuelve a intentarlo.");
}
