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

    /// <summary>
    /// Por ruta, margen sobre el precio: Σ(precio en COP − <c>costoFinal</c>) / Σ precio en COP de las rutas con costo
    /// y precio &gt; 0, 4 decimales; <c>null</c> si no hay ninguna.
    /// </summary>
    public decimal? MargenPonderado { get; init; }

    public decimal CosteoTotal { get; init; }
    public decimal CosteoGanadas { get; init; }
    public decimal ValorCotizadoCop { get; init; }
    public decimal ValorGanadoCop { get; init; }
    public decimal Ganancias { get; init; }
    public int CotizacionesUsd { get; init; }
    public decimal? TiempoPromedioCotizacionHoras { get; init; }
    public decimal? DesviacionPromedioMargen { get; init; }
    public decimal? DesviacionPromedioMargenAbs { get; init; }
    public int AjustesMargen { get; init; }

    /// <summary>Sin resultado y con <c>deadline</c> anterior a ahora; <c>null</c> si el esquema no tiene la columna (script 0004).</summary>
    public int? DeadlineVencidas { get; init; }

    /// <summary>
    /// Promedio simple del margen de política (<see cref="clsRutaMetrica.MargenPolitica"/>)
    /// de las rutas comparables, las mismas que se cotizaron: versión vigente, sin las rechazadas, con margen aplicado y con política.
    /// 4 decimales; <c>null</c> si no hay ninguna.
    /// </summary>
    public decimal? MargenPoliticaPromedio { get; init; }

    /// <summary>
    /// Promedio simple del margen aplicado (<see cref="clsRutaMetrica.MargenAplicado"/>) de
    /// exactamente las mismas rutas comparables de <see cref="MargenPoliticaPromedio"/>, para comparar aplicado simple contra política
    /// simple. No reemplaza a <see cref="MargenPromedio"/>, que es por cotización e incluye las rutas sin política. 4 decimales;
    /// <c>null</c> si no hay ninguna.
    /// </summary>
    public decimal? MargenAplicadoPromedio { get; init; }

    /// <summary>
    /// Margen de política de esas rutas ponderado por <c>costoFinal</c> (las de costo &gt; 0). 4 decimales; <c>null</c> si
    /// ninguna tiene costo.
    /// </summary>
    public decimal? MargenPoliticaPonderado { get; init; }

    /// <summary>
    /// Σ((aplicado − política) × costo) / Σ costo de las mismas rutas de <see cref="MargenPoliticaPonderado"/>, es decir, el
    /// margen aplicado ponderado por costo menos la política ponderada por costo. Base del semáforo (verde ≥ 0). No reemplaza a
    /// <see cref="DesviacionPromedioMargen"/>, que solo promedia los ajustes negociados (ADR-016).
    /// </summary>
    public decimal? DesviacionMargenPolitica { get; init; }

    /// <summary>Rutas con margen aplicado y sin margen de política guardado; no entran en la comparación.</summary>
    public int RutasSinMargenPolitica { get; init; }
}

/// <summary>
/// Un mes (fecha de negocio) de la serie del tablero (esquema <c>PuntoSerieMensual</c>). Los montos tienen la misma definición que en
/// <see cref="clsIndicadoresMetricas"/>: la suma de los meses del periodo da los montos de los indicadores del periodo.
/// </summary>
/// <param name="Mes">Mes en formato <c>AAAA-MM</c>.</param>
/// <param name="CosteoTotal">Σ <c>costo</c> en COP de las cotizaciones del mes.</param>
/// <param name="CosteoGanadas">Σ <c>costo</c> en COP de las ganadas del mes (el de la versión ganada).</param>
/// <param name="Ganancias">Σ(<c>valor_cop</c> − <c>costo</c>) de las ganadas del mes con precio y costo.</param>
public sealed record clsPuntoSerieMensual(
    string Mes,
    int Cantidad,
    int Ganadas,
    int Perdidas,
    decimal ValorCotizadoCop,
    decimal ValorGanadoCop,
    decimal CosteoTotal,
    decimal CosteoGanadas,
    decimal Ganancias);

/// <summary>Cotizaciones con un municipio de origen o destino (esquema <c>MetricaCiudad</c>).</summary>
public sealed record clsMetricaCiudad(string Ciudad, int Cantidad, int Ganadas, decimal ValorCotizadoCop, decimal ValorGanadoCop);

/// <summary>
/// Apertura por línea de negocio, por ruta (esquema <c>MetricaLineaNegocio</c>). Los conteos son cotizaciones
/// distintas con al menos una ruta de la línea (una cotización de tres líneas aparece en las tres); los valores son solo de esas rutas.
/// </summary>
/// <param name="LineaNegocioId"><c>MargenesPolitica.id</c>; <c>null</c> agrupa las rutas sin línea.</param>
/// <param name="Rutas">Rutas de la línea.</param>
/// <param name="ValorCotizadoCop">Σ del <c>precioFinalLinea</c> de cada ruta en COP (USD × TRM de su cotización, redondeado por ruta).</param>
/// <param name="Ganancias">Σ(valor en COP − <c>costoFinal</c>) de las rutas de cotizaciones ganadas con precio y costo.</param>
/// <param name="DesviacionPromedioMargen">Promedio de <c>MargenAjustes.diferencia</c> de los ajustes de esas rutas.</param>
public sealed record clsMetricaLineaNegocio(
    int? LineaNegocioId,
    string? LineaNegocio,
    int Cotizaciones,
    int Ganadas,
    int Perdidas,
    int Rutas,
    decimal CosteoTotal,
    decimal CosteoGanadas,
    decimal ValorCotizadoCop,
    decimal ValorGanadoCop,
    decimal Ganancias,
    decimal? MargenPonderado,
    int AjustesMargen,
    decimal? DesviacionPromedioMargen);

/// <summary>Concentración por municipio para el mapa (esquema <c>ConcentracionCiudades</c>).</summary>
/// <param name="Disponible"><c>false</c> si el esquema aún no tiene las columnas <c>origen</c>/<c>destino</c> (script 0004).</param>
public sealed record clsConcentracionCiudades(
    bool Disponible,
    IReadOnlyList<clsMetricaCiudad> PorDestino,
    IReadOnlyList<clsMetricaCiudad> PorOrigen,
    int SinDestino,
    int SinOrigen);
