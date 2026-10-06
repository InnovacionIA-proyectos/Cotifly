using Cotifly.Aplicacion.Cotizaciones.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Costeo.DTOs;

/// <summary>
/// Entrada de <c>POST /web/cotizaciones/{folio}/costeo/re-solicitar</c> (<c>ReSolicitudCosteoWeb</c>, HU-013.4): el motivo (obligatorio,
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
/// </summary>
public sealed record clsReSolicitudCosteo(bool VersionNueva, clsCotizacionResumen Cotizacion, string RowVersion);
