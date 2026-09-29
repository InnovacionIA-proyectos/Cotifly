namespace Cotifly.Dominio.Metricas;

/// <summary>
/// KPIs de un conjunto de cotizaciones (esquema <c>IndicadoresMetricas</c> del contrato; definiciones en ADR-016 §4).
/// Montos en COP con 2 decimales; fracciones de 0 a 1 con 4 decimales.
/// </summary>
public sealed record clsIndicadoresMetricas
{
    public int Cantidad { get; init; }
    public int Versiones { get; init; }
    public int Ganadas { get; init; }
    public int Perdidas { get; init; }
    public int SinResultado { get; init; }
    public int PendientesPorCotizar { get; init; }
    public decimal? Conversion { get; init; }
    public decimal? ConversionSobreCerradas { get; init; }
    public decimal? MargenPromedio { get; init; }
    public decimal CosteoTotal { get; init; }
    public decimal ValorCotizadoCop { get; init; }
    public decimal ValorGanadoCop { get; init; }
    public decimal Ganancias { get; init; }
    public int CotizacionesUsd { get; init; }
    public decimal? TiempoPromedioCotizacionHoras { get; init; }
    public decimal? DesviacionPromedioMargen { get; init; }
    public int AjustesMargen { get; init; }
}

/// <summary>Un mes (fecha de negocio) de la serie del tablero (esquema <c>PuntoSerieMensual</c>).</summary>
/// <param name="Mes">Mes en formato <c>AAAA-MM</c>.</param>
public sealed record clsPuntoSerieMensual(string Mes, int Cantidad, int Ganadas, int Perdidas, decimal ValorCotizadoCop, decimal ValorGanadoCop);

/// <summary>Cotizaciones con un municipio de origen o destino (esquema <c>MetricaCiudad</c>).</summary>
public sealed record clsMetricaCiudad(string Ciudad, int Cantidad, int Ganadas, decimal ValorCotizadoCop, decimal ValorGanadoCop);

/// <summary>Concentración por municipio para el mapa (esquema <c>ConcentracionCiudades</c>).</summary>
/// <param name="Disponible"><c>false</c> si el esquema aún no tiene las columnas <c>origen</c>/<c>destino</c> (script 0004).</param>
public sealed record clsConcentracionCiudades(
    bool Disponible,
    IReadOnlyList<clsMetricaCiudad> PorDestino,
    IReadOnlyList<clsMetricaCiudad> PorOrigen,
    int SinDestino,
    int SinOrigen);
