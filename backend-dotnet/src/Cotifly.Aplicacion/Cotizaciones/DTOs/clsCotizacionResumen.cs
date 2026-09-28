using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Aplicacion.Cotizaciones.DTOs;

/// <summary>
/// Fila de "Mis cotizaciones" (<c>CotizacionResumen</c> de <c>specs/openapi.yaml</c>). La Api la serializa en snake_case
/// (<c>nit_cliente</c>, <c>precio_final</c>...); los enums van con su código (<c>sin_enviar</c>, <c>politica_linea</c>).
/// </summary>
/// <param name="Cliente">Nombre del cliente (<c>clienteNombre</c>).</param>
/// <param name="LineaNegocio">Nombre de la línea (JOIN con <c>MargenesPolitica</c> por <c>lineaNegocioId</c>).</param>
/// <param name="PdfUrl"><c>{Pdf:BaseUrl}/pdfs/{archivo}</c>, o <c>null</c> si la cotización no tiene PDF.</param>
public sealed record clsCotizacionResumen(
    Guid Id,
    string Folio,
    int Version,
    string? NitCliente,
    string? OrigenCliente,
    string? OrigenMargen,
    string? Cliente,
    string? LineaNegocio,
    decimal? PrecioFinal,
    decimal? MargenAplicado,
    string Moneda,
    string? EstadoCosteo,
    string? MotivoEstadoCosteo,
    string EstadoEnvio,
    bool? Ganada,
    string? MotivoRechazo,
    string? PdfUrl,
    DateTimeOffset? CreatedAt)
{
    /// <summary>Resumen de una cotización con el nombre de su línea y la base pública de los PDF.</summary>
    public static clsCotizacionResumen Desde(clsCotizacion cotizacion, string? lineaNegocio, string? baseUrlPdf)
    {
        ArgumentNullException.ThrowIfNull(cotizacion);
        return new clsCotizacionResumen(
            cotizacion.Id,
            cotizacion.Folio,
            cotizacion.Version,
            cotizacion.NitCliente,
            cotizacion.OrigenCliente is { } origenCliente ? clsCodigoEnum.ACodigo(origenCliente) : null,
            cotizacion.OrigenMargen is { } origenMargen ? clsCodigoEnum.ACodigo(origenMargen) : null,
            cotizacion.ClienteNombre,
            lineaNegocio,
            cotizacion.PrecioFinal,
            cotizacion.MargenAplicado,
            cotizacion.Moneda ?? clsCotizacion.MonedaPesos,
            cotizacion.EstadoCosteo is { } estadoCosteo ? clsCodigoEnum.ACodigo(estadoCosteo) : null,
            cotizacion.MotivoEstadoCosteo,
            clsCodigoEnum.ACodigo(cotizacion.EstadoEnvio ?? Dominio.Cotizaciones.EstadoEnvio.SinEnviar),
            cotizacion.Ganada,
            cotizacion.MotivoRechazo,
            clsUrlPdf.DesdeReferencia(baseUrlPdf, cotizacion.PdfPath),
            cotizacion.CreatedAt == default ? null : cotizacion.CreatedAt);
    }
}

/// <summary>Cotización con el nombre de su línea de negocio (lectura con JOIN para "Mis cotizaciones").</summary>
public sealed record clsCotizacionConLinea(clsCotizacion Cotizacion, string? LineaNegocio);

/// <summary><c>MisCotizaciones</c>: <c>{total, cotizaciones}</c>, máximo 100, de la más reciente a la más antigua.</summary>
public sealed record clsMisCotizaciones(int Total, IReadOnlyList<clsCotizacionResumen> Cotizaciones);

/// <summary>
/// <c>DatosNuevaVersion</c>: datos de una cotización existente para prellenar una versión nueva. No se persiste nada;
/// <paramref name="CotizacionOrigenId"/> es solo el id consultado (sin FK en la versión nueva).
/// </summary>
/// <param name="FolioBase">Folio de la v1 (sin <c>-vN</c>).</param>
/// <param name="VersionSiguiente">Versión que tendría la nueva fila (máxima de la familia + 1), informativa: la definitiva la
/// asigna <c>IClsGeneradorFolioService</c> al crearla.</param>
public sealed record clsDatosNuevaVersion(
    Guid CotizacionOrigenId,
    string FolioBase,
    int VersionSiguiente,
    string? ClienteNombre,
    string? NitCliente,
    string? OrigenCliente,
    string? LineaNegocio,
    decimal? Costo,
    decimal? MargenPolitica,
    decimal? MargenAplicado,
    string? OrigenMargen,
    string? MetodoCalculo,
    string? Moneda);
