using System.Text.Json.Serialization;
using Cotifly.Api.Comun;
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
    /// <summary>
    /// Retirado del contrato (decisión 1 de HU-021, #138): ya no está en <c>openapi.yaml</c>. Si un frontend anterior lo manda, se ignora
    /// sin error y queda un aviso en el log (<see cref="CamposRetirados"/>).
    /// </summary>
    [clsCampoRetirado("decisión 1 de HU-021")]
    public decimal? ValorEstimado { get; init; }

    /// <summary>Retirado del contrato (decisión 1 de HU-021, #138): igual que <see cref="ValorEstimado"/>.</summary>
    [clsCampoRetirado("decisión 1 de HU-021")]
    public string? Referencia { get; init; }

    /// <summary>Nombres snake_case de los campos retirados que llegaron (para el aviso del log).</summary>
    public IReadOnlyList<string> CamposRetirados() =>
        [.. new[] { (ValorEstimado is not null, "valor_estimado"), (Referencia is not null, "referencia") }.Where(c => c.Item1).Select(c => c.Item2)];
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
        [.. (Lineas ?? []).Select(l => l is null ? null! : l.AEntrada())],
        archivo,
        Moneda);
}

/// <summary>
/// <c>LineaLicitacionWeb</c>: los campos destino del mapeo (los de <c>CapturaLineaRequest</c>) y la fila del análisis. HU-021.6: cada
/// fila es una ruta; puede traer su línea de negocio, los ids de las ciudades del contenedor, mercancías con <c>id_tipo_mercancia</c> y
/// servicios conexos del catálogo de Nexo. Vehículo y carrocería ya no se mapean (decisión 7): si llegan, se ignoran.
/// </summary>
public sealed class clsLineaLicitacionWebRequest
{
    public int? Fila { get; init; }

    /// <summary>Línea de negocio de esta ruta; sin ella, la de la licitación.</summary>
    public string? LineaNegocio { get; init; }
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

    /// <summary>HU-032 (#197, ADR-022): el vehículo elegido de la ruta (código de Nexo). Obligatorio sin mercancías cubicables, salvo en Contenedor (<c>VEHICULO_REQUERIDO</c>); es el respaldo del cubicador: si el cubicador asigna, manda el cubicador. La carrocería es opcional.</summary>
    public string? TipoVehiculo { get; init; }

    /// <summary>HU-032 (#197, ADR-022): el vehículo elegido de la ruta (código de Nexo). Obligatorio sin mercancías cubicables, salvo en Contenedor (<c>VEHICULO_REQUERIDO</c>); es el respaldo del cubicador: si el cubicador asigna, manda el cubicador. La carrocería es opcional.</summary>
    public string? Carroceria { get; init; }
    public string? Tipologia { get; init; }
    public string? ClaseViaje { get; init; }
    public string? ClaseContenedor { get; init; }
    public bool? Exclusivo { get; init; }
    public bool? Consolidado { get; init; }
    public bool? RetornaContenedor { get; init; }
    public string? CiudadDevolucionContenedor { get; init; }
    public int? CiudadDevolucionMunicipioId { get; init; }
    public bool? LevanteContenedorDiferente { get; init; }
    public string? CiudadLevanteContenedor { get; init; }
    public int? CiudadLevanteMunicipioId { get; init; }

    /// <summary>Valor de un levante del contenedor (nombre anterior: <see cref="ValorAprovisionamiento"/>; si llegan los dos, vale este).</summary>
    public decimal? ValorLevanteContenedor { get; init; }
    public decimal? ValorAprovisionamiento { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }

    /// <summary>HU-030.1 (#184): peso total con tara de la fila (solo con <c>clase_viaje = Contenedor</c>), como en la captura.</summary>
    public decimal? PesoConTaraKg { get; init; }

    /// <summary>Mercancías de la ruta (nombre anterior: <see cref="Posiciones"/>; si llegan las dos, valen estas).</summary>
    public IReadOnlyList<clsPosicionCargaWebRequest?>? Mercancias { get; init; }
    public IReadOnlyList<clsPosicionCargaWebRequest?>? Posiciones { get; init; }
    public IReadOnlyList<clsConexoCapturaWebRequest?>? ServiciosConexos { get; init; }

    public clsLineaLicitacionEntrada AEntrada() => new(Fila, ALinea(), LineaNegocio, clsConexoCapturaWebRequest.AEntradas(ServiciosConexos));

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
        CiudadDevolucionMunicipioId = CiudadDevolucionMunicipioId,
        LevanteContenedorDiferente = LevanteContenedorDiferente,
        CiudadLevanteContenedor = CiudadLevanteContenedor,
        CiudadLevanteMunicipioId = CiudadLevanteMunicipioId,
        ValorAprovisionamiento = ValorLevanteContenedor ?? ValorAprovisionamiento,
        PesoKg = PesoKg,
        ValorMercancia = ValorMercancia,
        PesoConTaraKg = PesoConTaraKg,
        Posiciones = clsPosicionCargaWebRequest.APosiciones(Mercancias ?? Posiciones),
    };
}

/// <summary>
/// <c>ReporteLicitacionInvalida</c>: 400 de <c>POST /web/licitaciones</c> con todas las filas que fallaron. Mismo <c>error</c> que
/// <c>ErrorNegocio</c> (<c>LICITACION_INVALIDA</c>) más <c>errores[]</c>.
/// </summary>
public sealed record clsReporteLicitacionWeb(
    [property: JsonPropertyName("error")] Comun.clsErrorNegocioWeb.clsCuerpo Error,
    [property: JsonPropertyName("errores")] IReadOnlyList<clsErrorFilaLicitacion> Errores);

/// <summary>
/// HU-023.5 (#160): cuerpo opcional de <c>POST /web/licitaciones/{folio}/cubicacion/reintentar</c>. <c>usar_respaldo = true</c> acepta
/// las rutas fallidas con un camión sin vehículo (lo resuelve la Mesa de Costeo de Nexo) en vez de volver a llamar al cubicador.
/// </summary>
public sealed class clsReintentoCubicacionWebRequest
{
    public bool? UsarRespaldo { get; init; }
}
