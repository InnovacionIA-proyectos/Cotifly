using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Entrada de <c>POST /web/cotizaciones/{folio}/costeo/re-solicitar</c> (<c>ReSolicitudCosteoWeb</c>): el motivo (obligatorio,
/// queda en la auditoría), los cambios de carga (<c>null</c> = sin cambios) y si se pide la versión nueva sin cambios de carga.
/// </summary>
public sealed record clsEntradaReSolicitudCosteo(string? Motivo, clsCambiosCarga? DatosCarga, bool NuevaVersion)
{
    /// <summary>Largo máximo del motivo (contrato: 300).</summary>
    public const int LargoMaximoMotivo = 300;

    /// <summary>¿Crea la versión <c>-vN</c>? Con cambios de carga siempre; sin ellos, solo con <c>nueva_version = true</c>.</summary>
    public bool VersionNueva => DatosCarga is not null || NuevaVersion;
}

/// <summary>
/// Resultado de la re-solicitud: la cotización que quedó en Nexo (la misma, 200, o la versión nueva, 201) como <c>CotizacionResumen</c>
/// y su versión de negocio para el <c>ETag</c>. Es lo que se guarda para repetir la respuesta con la misma <c>Idempotency-Key</c>.
/// <paramref name="ConexosDescartados"/>: la re-solicitud no descarta conexos y siempre es vacía (se conserva por compatibilidad del
/// contrato). <c>null</c> en una respuesta guardada sin el campo (se responde vacía).
/// </summary>
public sealed record clsReSolicitudCosteo(
    bool VersionNueva, clsCotizacionResumen Cotizacion, string RowVersion, IReadOnlyList<string>? ConexosDescartados = null)
{
    /// <summary>
    /// Nombres de los servicios conexos que la cotización resultante conserva (en el mismo folio) o copió de la original
    /// (en la <c>-vN</c>), sin montos. Siguen en el precio de su ruta; a la cotización nueva de Nexo los manda una vez el envío diferido. <c>null</c> en una
    /// respuesta guardada sin el campo (se responde vacía).
    /// </summary>
    public IReadOnlyList<string>? ConexosConservados { get; init; }

    /// <summary>Orden de las rutas de la <c>-vN</c> cuyos camiones se volvieron a planificar (solo para la auditoría).</summary>
    public IReadOnlyList<int>? RutasReplanificadas { get; init; }

    /// <summary>
    /// Aviso al comercial de los camiones rechazados por la Mesa de Costeo que NO se reenviaron
    /// (<see cref="Services.clsReSolicitudCosteoService.AvisoRechazos"/>); <c>null</c> si no se omitió nada. Va en <c>aviso_rechazos</c>.
    /// </summary>
    public string? AvisoRechazos { get; init; }
}
