using System.Text.Json.Serialization;
using Cotifly.Api.Costeo.DTOs;
using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Api.Licitaciones.DTOs;

/// <summary>
/// <c>LicitacionWeb</c> (snake_case): el encabezado de la licitación y sus líneas ya mapeadas por el frontend (HU-011.2,
/// <c>docs/conexion_nexo/HU-011-formato-excel-licitacion.md</c>). El tipo es siempre <c>licitacion</c>. Los límites los valida el caso de uso para
/// devolver el reporte por fila.
/// </summary>
public sealed class clsLicitacionWebRequest
{
    public string? NitCliente { get; init; }
    public string? ClienteNombre { get; init; }
    public string? LineaNegocio { get; init; }

    /// <summary>ISO 8601; sin zona se interpreta en hora de Bogotá (G4, <see cref="Comun.clsConvertidorFechaBogota"/>).</summary>
    [JsonConverter(typeof(Comun.clsConvertidorFechaBogota))]
    public DateTimeOffset? Deadline { get; init; }
    /// <summary>Obsoleto (decisión 1 de HU-021): se acepta y se ignora; deja de enviarse a Nexo. Lo retira el contrato de #138.</summary>
    public decimal? ValorEstimado { get; init; }
    /// <summary>Obsoleto (decisión 1 de HU-021): se acepta y se ignora. Lo retira el contrato de #138.</summary>
    public string? Referencia { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }

    /// <summary><c>notas</c> del encabezado de Nexo (máx. 2000, G7): nota para la Mesa, igual que en <c>SolicitudCosteoWeb</c>.</summary>
    public string? Notas { get; init; }
    public string? Hoja { get; init; }

    /// <summary><c>COP</c> (por defecto) o <c>USD</c> (ADR-014 §5); otro valor o una cadena vacía es un error del reporte (campo <c>moneda</c>).</summary>
    public string? Moneda { get; init; }

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
            HorasCargue = HorasCargue,
            HorasDescargue = HorasDescargue,
            Notas = Notas,
        },
        Hoja,
        [.. (Lineas ?? []).Select(l => l is null ? null! : new clsLineaLicitacionEntrada(l.Fila, l.ALinea()))],
        archivo,
        Moneda);
}

/// <summary><c>LineaLicitacionWeb</c>: los campos destino del mapeo (los de <c>CapturaLineaRequest</c>) y la fila del análisis.</summary>
public sealed class clsLineaLicitacionWebRequest
{
    public int? Fila { get; init; }
    public string? Origen { get; init; }
    public string? Destino { get; init; }

    /// <summary>
    /// <c>origenMunicipioId</c> (G6, HU-013.11): <c>id</c> de <c>GET /web/municipios</c> si el frontend ya lo resolvió. Sin él, Cotifly
    /// lo resuelve por el texto de <see cref="Origen"/> en el catálogo de municipios de Nexo (ambiguo o sin coincidencia = error de la fila).
    /// </summary>
    public int? OrigenMunicipioId { get; init; }

    /// <summary><c>destinoMunicipioId</c>: igual que <see cref="OrigenMunicipioId"/>, para el destino.</summary>
    public int? DestinoMunicipioId { get; init; }

    /// <summary>Horas de cargue de esta fila (G5); sin valor, las del encabezado.</summary>
    public decimal? HorasCargue { get; init; }

    /// <summary>Horas de descargue de esta fila (G5); sin valor, las del encabezado.</summary>
    public decimal? HorasDescargue { get; init; }
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
        OrigenMunicipioId = OrigenMunicipioId,
        DestinoMunicipioId = DestinoMunicipioId,
        HorasCargue = HorasCargue,
        HorasDescargue = HorasDescargue,
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
