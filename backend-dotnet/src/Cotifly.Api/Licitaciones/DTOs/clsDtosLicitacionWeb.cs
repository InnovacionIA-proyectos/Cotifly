using System.Text.Json.Serialization;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Api.Licitaciones.DTOs;

/// <summary>
/// <c>LicitacionWeb</c> (snake_case): el encabezado de la licitación y sus líneas ya mapeadas por el frontend (HU-011.2,
/// <c>docs/HU-011-formato-excel-licitacion.md</c>). El tipo es siempre <c>licitacion</c>. Los límites los valida el caso de uso para
/// devolver el reporte por fila.
/// </summary>
public sealed class clsLicitacionWebRequest
{
    public string? NitCliente { get; init; }
    public string? ClienteNombre { get; init; }
    public string? LineaNegocio { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public decimal? ValorEstimado { get; init; }
    public string? Referencia { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }
    public string? Hoja { get; init; }
    public IReadOnlyList<clsLineaLicitacionWebRequest?>? Lineas { get; init; }

    /// <summary>Entrada del caso de uso, con el archivo original si vino en la parte <c>archivo</c> del multipart.</summary>
    public clsEntradaLicitacion AEntrada(clsArchivoSubido? archivo) => new(
        NitCliente,
        ClienteNombre,
        LineaNegocio,
        new clsEncabezadoSolicitudCosteo
        {
            Tipo = TipoSolicitud.Licitacion,
            Deadline = Deadline,
            ValorEstimado = ValorEstimado,
            Referencia = Referencia,
            HorasCargue = HorasCargue,
            HorasDescargue = HorasDescargue,
        },
        Hoja,
        [.. (Lineas ?? []).Select(l => l is null ? null! : new clsLineaLicitacionEntrada(l.Fila, l.ALinea()))],
        archivo);
}

/// <summary><c>LineaLicitacionWeb</c>: los campos destino del mapeo (los de <c>CapturaLineaRequest</c>) y la fila del análisis.</summary>
public sealed class clsLineaLicitacionWebRequest
{
    public int? Fila { get; init; }
    public string? Origen { get; init; }
    public string? Destino { get; init; }
    public string? TipoVehiculo { get; init; }
    public string? Carroceria { get; init; }
    public string? Tipologia { get; init; }
    public string? ClaseViaje { get; init; }
    public string? ClaseContenedor { get; init; }
    public bool? Exclusivo { get; init; }
    public bool? Consolidado { get; init; }
    public bool? RetornaContenedor { get; init; }
    public string? CiudadDevolucionContenedor { get; init; }
    public bool? LevanteContenedorDiferente { get; init; }
    public string? CiudadLevanteContenedor { get; init; }
    public decimal? ValorAprovisionamiento { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }
    public IReadOnlyList<clsPosicionCargaWebRequest?>? Posiciones { get; init; }

    public clsLineaSolicitudCosteo ALinea() => new()
    {
        Origen = Origen ?? string.Empty,
        Destino = Destino ?? string.Empty,
        TipoVehiculo = TipoVehiculo,
        Carroceria = Carroceria,
        Tipologia = Tipologia,
        ClaseViaje = ClaseViaje,
        ClaseContenedor = ClaseContenedor,
        Exclusivo = Exclusivo,
        Consolidado = Consolidado,
        RetornaContenedor = RetornaContenedor,
        CiudadDevolucionContenedor = CiudadDevolucionContenedor,
        LevanteContenedorDiferente = LevanteContenedorDiferente,
        CiudadLevanteContenedor = CiudadLevanteContenedor,
        ValorAprovisionamiento = ValorAprovisionamiento,
        PesoKg = PesoKg,
        ValorMercancia = ValorMercancia,
        Posiciones = [.. (Posiciones ?? []).Select(p => p is null
            ? null!
            : new clsPosicionCarga(p.Descripcion, p.Cantidad, p.LargoCm, p.AnchoCm, p.AltoCm, p.PesoKg))],
    };
}

/// <summary>
/// <c>ReporteLicitacionInvalida</c>: 400 de <c>POST /web/licitaciones</c> con todas las filas que fallaron. Mismo <c>error</c> que
/// <c>ErrorNegocio</c> (<c>LICITACION_INVALIDA</c>) más <c>errores[]</c>.
/// </summary>
public sealed record clsReporteLicitacionWeb(
    [property: JsonPropertyName("error")] Comun.clsErrorNegocioWeb.clsCuerpo Error,
    [property: JsonPropertyName("errores")] IReadOnlyList<clsErrorFilaLicitacion> Errores);
