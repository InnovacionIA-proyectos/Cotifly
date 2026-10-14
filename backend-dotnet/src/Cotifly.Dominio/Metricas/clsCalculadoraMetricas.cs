using System.Globalization;

namespace Cotifly.Dominio.Metricas;

/// <summary>
/// Reglas puras del tablero de métricas (HU-010.1, ADR-016 §4): agrega cotizaciones (<see cref="clsCotizacionMetrica"/>, una por
/// cadena de versiones) en indicadores, serie mensual y concentración por ciudad. El periodo y el alcance ya vienen aplicados por
/// quien consulta. Redondeo siempre half-to-even.
/// </summary>
/// <remarks>
/// Partición del universo: <c>cantidad = ganadas + perdidas + sin_resultado + pendientes_por_cotizar</c>. Las ganadas y perdidas
/// cuentan por su resultado aunque no tengan precio; <c>sin_resultado</c> son las que tienen precio y aún no tienen resultado, y
/// <c>pendientes_por_cotizar</c> las que no tienen ni precio ni resultado.
/// HU-021.10 (decisión 8): los conteos son por cotización (una cotización con rutas de varias líneas de negocio cuenta una vez); los
/// valores salen de sus rutas (<see cref="clsCotizacionMetrica.Rutas"/>): el margen ponderado y la apertura por línea
/// (<see cref="PorLineaNegocio"/>) son por ruta.
/// </remarks>
public static class clsCalculadoraMetricas
{
    public const int DecimalesFraccion = 4;
    public const int DecimalesMonto = 2;
    public const int DecimalesHoras = 2;

    /// <summary>
    /// Indicadores de <paramref name="filas"/>. <paramref name="ahora"/> decide qué deadlines ya vencieron; con
    /// <paramref name="deadlineDisponible"/> en <c>false</c> (sin la columna del 0004) <see cref="clsIndicadoresMetricas.DeadlineVencidas"/> es <c>null</c>.
    /// </summary>
    public static clsIndicadoresMetricas Indicadores(IEnumerable<clsCotizacionMetrica> filas, DateTimeOffset ahora, bool deadlineDisponible)
    {
        ArgumentNullException.ThrowIfNull(filas);
        var universo = filas.ToList();

        var ganadas = universo.Where(f => f.Ganada == true).ToList();
        var perdidas = universo.Count(f => f.Ganada == false);
        var margenes = universo.Where(f => f.MargenAplicado.HasValue).Select(f => f.MargenAplicado!.Value).ToList();
        var tiempos = universo
            .Where(f => f.CosteoListoAt.HasValue && f.CreatedAtVigente.HasValue && f.CosteoListoAt >= f.CreatedAtVigente)
            .Select(f => (decimal)(f.CosteoListoAt!.Value - f.CreatedAtVigente!.Value).TotalHours)
            .ToList();
        var numDiferencias = universo.Sum(f => f.NumDiferencias);

        return new clsIndicadoresMetricas
        {
            Cantidad = universo.Count,
            Versiones = universo.Sum(f => f.Versiones),
            Ganadas = ganadas.Count,
            Perdidas = perdidas,
            SinResultado = universo.Count(f => f.Ganada is null && f.PrecioFinal is not null),
            PendientesPorCotizar = universo.Count(f => f.Ganada is null && f.PrecioFinal is null),
            Conversion = Fraccion(ganadas.Count, universo.Count),
            ConversionSobreCerradas = Fraccion(ganadas.Count, ganadas.Count + perdidas),
            MargenPromedio = margenes.Count == 0 ? null : Redondear(margenes.Average(), DecimalesFraccion),
            MargenPonderado = MargenPonderadoDe(universo.SelectMany(f => f.Rutas.Select(r => (f, r)))),
            CosteoTotal = Redondear(universo.Sum(f => f.Costo ?? 0m), DecimalesMonto),
            // B3 (#173): lo ganado sale de la versión ganada, no de la vigente.
            CosteoGanadas = Redondear(ganadas.Sum(f => f.CostoResultado ?? 0m), DecimalesMonto),
            ValorCotizadoCop = Redondear(universo.Sum(f => f.ValorCop ?? 0m), DecimalesMonto),
            ValorGanadoCop = Redondear(ganadas.Sum(f => f.ValorResultadoCop ?? 0m), DecimalesMonto),
            Ganancias = Redondear(
                ganadas.Where(f => f.ValorResultadoCop.HasValue && f.CostoResultado.HasValue)
                    .Sum(f => f.ValorResultadoCop!.Value - f.CostoResultado!.Value),
                DecimalesMonto),
            CotizacionesUsd = universo.Count(f => !f.EsPesos),
            TiempoPromedioCotizacionHoras = tiempos.Count == 0 ? null : Redondear(tiempos.Average(), DecimalesHoras),
            DesviacionPromedioMargen = numDiferencias == 0
                ? null
                : Redondear(universo.Sum(f => f.SumaDiferencias ?? 0m) / numDiferencias, DecimalesFraccion),
            DesviacionPromedioMargenAbs = numDiferencias == 0
                ? null
                : Redondear(universo.Sum(f => f.SumaDiferenciasAbsolutas ?? 0m) / numDiferencias, DecimalesFraccion),
            AjustesMargen = universo.Sum(f => f.NumAjustes),
            DeadlineVencidas = deadlineDisponible ? universo.Count(f => f.Ganada is null && f.Deadline < ahora) : null,
        };
    }

    /// <summary>
    /// Serie por mes de negocio (<paramref name="fechaNegocio"/> convierte la primera creación de la cadena a la fecha de Bogotá)
    /// con todos los meses de <paramref name="desde"/> a <paramref name="hasta"/>, también los vacíos.
    /// </summary>
    public static IReadOnlyList<clsPuntoSerieMensual> SerieMensual(
        IEnumerable<clsCotizacionMetrica> filas, DateOnly desde, DateOnly hasta, Func<DateTimeOffset, DateOnly> fechaNegocio)
    {
        ArgumentNullException.ThrowIfNull(filas);
        ArgumentNullException.ThrowIfNull(fechaNegocio);
        if (hasta < desde)
        {
            return [];
        }

        var porMes = filas
            .GroupBy(f => Mes(fechaNegocio(f.CreatedAt)))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var serie = new List<clsPuntoSerieMensual>();
        for (var mes = new DateOnly(desde.Year, desde.Month, 1); mes <= hasta; mes = mes.AddMonths(1))
        {
            var clave = Mes(mes);
            var delMes = porMes.GetValueOrDefault(clave) ?? [];
            var ganadas = delMes.Where(f => f.Ganada == true).ToList();
            serie.Add(new clsPuntoSerieMensual(
                clave,
                delMes.Count,
                ganadas.Count,
                delMes.Count(f => f.Ganada == false),
                Redondear(delMes.Sum(f => f.ValorCop ?? 0m), DecimalesMonto),
                Redondear(ganadas.Sum(f => f.ValorResultadoCop ?? 0m), DecimalesMonto)));
        }

        return serie;
    }

    /// <summary>
    /// Concentración por municipio de destino y de origen (texto recortado, sin distinguir mayúsculas; se muestra la primera
    /// forma encontrada). Ordenada por cantidad descendente y luego por nombre. Sin columnas de ciudad (<paramref name="disponible"/>
    /// en <c>false</c>) las listas van vacías.
    /// </summary>
    public static clsConcentracionCiudades Concentracion(IEnumerable<clsCotizacionMetrica> filas, bool disponible)
    {
        ArgumentNullException.ThrowIfNull(filas);
        var universo = filas.ToList();
        if (!disponible)
        {
            return new clsConcentracionCiudades(false, [], [], universo.Count, universo.Count);
        }

        return new clsConcentracionCiudades(
            true,
            PorCiudad(universo, f => f.Destino),
            PorCiudad(universo, f => f.Origen),
            universo.Count(f => string.IsNullOrWhiteSpace(f.Destino)),
            universo.Count(f => string.IsNullOrWhiteSpace(f.Origen)));
    }

    /// <summary>
    /// Apertura por línea de negocio, por ruta (HU-021.10, decisión 8). Agrupa las rutas de <paramref name="filas"/> por
    /// <see cref="clsRutaMetrica.LineaNegocioId"/>:
    /// <list type="bullet">
    /// <item><c>cotizaciones</c>/<c>ganadas</c>/<c>perdidas</c> = cotizaciones distintas con al menos una ruta de la línea (la cotización
    /// de tres líneas cuenta en las tres; su resultado es el de la cotización);</item>
    /// <item><c>costeo_total</c> = Σ <c>costoFinal</c>; <c>valor_cotizado_cop</c> = Σ <c>precioFinalLinea</c> en COP con la TRM de su
    /// cotización (<see cref="clsCotizacionMetrica.ACop"/>, redondeado por ruta); las de ganadas, solo las rutas de cotizaciones
    /// ganadas; <c>ganancias</c> = Σ(valor en COP − costo) de esas rutas con precio y costo;</item>
    /// <item><c>margen_ponderado</c> = Σ(precio en COP − costo) / Σ precio en COP de las rutas con costo y precio &gt; 0;</item>
    /// <item><c>ajustes_margen</c> y <c>desviacion_promedio_margen</c> = los <c>MargenAjustes</c> de esas rutas.</item>
    /// </list>
    /// Orden: <c>valor_cotizado_cop</c> descendente, luego el nombre; las rutas sin línea al final.
    /// </summary>
    public static IReadOnlyList<clsMetricaLineaNegocio> PorLineaNegocio(IEnumerable<clsCotizacionMetrica> filas)
    {
        ArgumentNullException.ThrowIfNull(filas);
        return [.. filas
            .SelectMany(f => f.Rutas.Select(r => (Cotizacion: f, Ruta: r)))
            .GroupBy(x => x.Ruta.LineaNegocioId)
            .Select(g =>
            {
                // Por referencia: dos cotizaciones con los mismos valores son dos cotizaciones.
                var cotizaciones = g.Select(x => x.Cotizacion).Distinct<clsCotizacionMetrica>(ReferenceEqualityComparer.Instance).ToList();
                var ganadas = g.Where(x => x.Cotizacion.Ganada == true).ToList();
                var numDiferencias = g.Sum(x => x.Ruta.NumDiferencias);
                return new clsMetricaLineaNegocio(
                    g.Key,
                    g.Select(x => x.Ruta.LineaNegocio).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    cotizaciones.Count,
                    cotizaciones.Count(c => c.Ganada == true),
                    cotizaciones.Count(c => c.Ganada == false),
                    g.Count(),
                    Redondear(g.Sum(x => x.Ruta.Costo ?? 0m), DecimalesMonto),
                    Redondear(ganadas.Sum(x => x.Ruta.Costo ?? 0m), DecimalesMonto),
                    Redondear(g.Sum(x => x.Cotizacion.ACop(x.Ruta.PrecioFinal) ?? 0m), DecimalesMonto),
                    Redondear(ganadas.Sum(x => x.Cotizacion.ACop(x.Ruta.PrecioFinal) ?? 0m), DecimalesMonto),
                    Redondear(
                        ganadas
                            .Select(x => (Valor: x.Cotizacion.ACop(x.Ruta.PrecioFinal), x.Ruta.Costo))
                            .Where(x => x.Valor.HasValue && x.Costo.HasValue)
                            .Sum(x => x.Valor!.Value - x.Costo!.Value),
                        DecimalesMonto),
                    MargenPonderadoDe(g),
                    g.Sum(x => x.Ruta.NumAjustes),
                    numDiferencias == 0 ? null : Redondear(g.Sum(x => x.Ruta.SumaDiferencias ?? 0m) / numDiferencias, DecimalesFraccion));
            })
            .OrderBy(l => l.LineaNegocioId is null ? 1 : 0)
            .ThenByDescending(l => l.ValorCotizadoCop)
            .ThenBy(l => l.LineaNegocio ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(l => l.LineaNegocioId)];
    }

    /// <summary>Mes <c>AAAA-MM</c> de una fecha.</summary>
    public static string Mes(DateOnly fecha) => fecha.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static List<clsMetricaCiudad> PorCiudad(List<clsCotizacionMetrica> universo, Func<clsCotizacionMetrica, string?> ciudad) =>
        [.. universo
            .Where(f => !string.IsNullOrWhiteSpace(ciudad(f)))
            .GroupBy(f => ciudad(f)!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var ganadas = g.Where(f => f.Ganada == true).ToList();
                return new clsMetricaCiudad(
                    g.Key,
                    g.Count(),
                    ganadas.Count,
                    Redondear(g.Sum(f => f.ValorCop ?? 0m), DecimalesMonto),
                    Redondear(ganadas.Sum(f => f.ValorResultadoCop ?? 0m), DecimalesMonto));
            })
            .OrderByDescending(c => c.Cantidad)
            .ThenBy(c => c.Ciudad, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Margen agregado sobre el precio: Σ(precio en COP − costo) / Σ precio en COP de las rutas con costo &gt; 0 y precio &gt; 0 (4 decimales);
    /// <c>null</c> si no hay ninguna. El precio de la ruta se pasa a COP con la TRM de su cotización (<see cref="clsCotizacionMetrica.ACop"/>),
    /// porque el costo siempre está en COP.
    /// </summary>
    private static decimal? MargenPonderadoDe(IEnumerable<(clsCotizacionMetrica Cotizacion, clsRutaMetrica Ruta)> rutas)
    {
        var validas = rutas
            .Select(x => (Precio: x.Cotizacion.ACop(x.Ruta.PrecioFinal), x.Ruta.Costo))
            .Where(x => x.Costo > 0m && x.Precio > 0m)
            .ToList();
        var precio = validas.Sum(x => x.Precio!.Value);
        return precio == 0m ? null : Redondear((precio - validas.Sum(x => x.Costo!.Value)) / precio, DecimalesFraccion);
    }

    private static decimal? Fraccion(int numerador, int denominador) =>
        denominador == 0 ? null : Redondear((decimal)numerador / denominador, DecimalesFraccion);

    private static decimal Redondear(decimal valor, int decimales) => Math.Round(valor, decimales, MidpointRounding.ToEven);
}
