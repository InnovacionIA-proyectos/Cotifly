using System.Globalization;

namespace Cotifly.Dominio.Metricas;

/// <summary>
/// Reglas puras del tablero de métricas (HU-010.1, ADR-016 §4): agrega filas de <see cref="clsCotizacionMetrica"/> en
/// indicadores, serie mensual y concentración por ciudad. El universo son las filas vigentes (<see cref="clsCotizacionMetrica.EsVigente"/>)
/// que recibe; el periodo y el alcance ya vienen aplicados por quien consulta. Redondeo siempre half-to-even.
/// </summary>
public static class clsCalculadoraMetricas
{
    public const int DecimalesFraccion = 4;
    public const int DecimalesMonto = 2;
    public const int DecimalesHoras = 2;

    /// <summary>Indicadores de las filas: el universo son las vigentes; <see cref="clsIndicadoresMetricas.Versiones"/> cuenta todas.</summary>
    public static clsIndicadoresMetricas Indicadores(IEnumerable<clsCotizacionMetrica> filas)
    {
        ArgumentNullException.ThrowIfNull(filas);
        var todas = filas.ToList();
        var universo = todas.Where(f => f.EsVigente).ToList();

        var ganadas = universo.Where(f => f.Ganada == true).ToList();
        var perdidas = universo.Count(f => f.Ganada == false);
        var margenes = universo.Where(f => f.MargenAplicado.HasValue).Select(f => f.MargenAplicado!.Value).ToList();
        var tiempos = universo
            .Where(f => f.CosteoListoAt.HasValue)
            .Select(f => (decimal)(f.CosteoListoAt!.Value - f.CreatedAt).TotalHours)
            .ToList();
        var numDiferencias = universo.Sum(f => f.NumDiferencias);
        var sumaDiferencias = universo.Sum(f => f.SumaDiferencias ?? 0m);

        return new clsIndicadoresMetricas
        {
            Cantidad = universo.Count,
            Versiones = todas.Count,
            Ganadas = ganadas.Count,
            Perdidas = perdidas,
            SinResultado = universo.Count(f => f.Ganada is null),
            PendientesPorCotizar = universo.Count(f => f.PrecioFinal is null),
            Conversion = Fraccion(ganadas.Count, universo.Count),
            ConversionSobreCerradas = Fraccion(ganadas.Count, ganadas.Count + perdidas),
            MargenPromedio = margenes.Count == 0 ? null : Redondear(margenes.Average(), DecimalesFraccion),
            CosteoTotal = Redondear(universo.Sum(f => f.Costo ?? 0m), DecimalesMonto),
            ValorCotizadoCop = Redondear(universo.Sum(f => f.PrecioCop ?? 0m), DecimalesMonto),
            ValorGanadoCop = Redondear(ganadas.Sum(f => f.PrecioCop ?? 0m), DecimalesMonto),
            Ganancias = Redondear(
                ganadas.Where(f => f.PrecioCop.HasValue && f.Costo.HasValue).Sum(f => f.PrecioCop!.Value - f.Costo!.Value),
                DecimalesMonto),
            CotizacionesUsd = universo.Count(f => !f.EsPesos),
            TiempoPromedioCotizacionHoras = tiempos.Count == 0 ? null : Redondear(tiempos.Average(), DecimalesHoras),
            DesviacionPromedioMargen = numDiferencias == 0 ? null : Redondear(sumaDiferencias / numDiferencias, DecimalesFraccion),
            AjustesMargen = universo.Sum(f => f.NumAjustes),
        };
    }

    /// <summary>
    /// Serie por mes de negocio (<paramref name="fechaNegocio"/> convierte <c>createdAt</c> a la fecha de Bogotá) con todos los
    /// meses de <paramref name="desde"/> a <paramref name="hasta"/>, también los vacíos. Solo cuenta las filas vigentes.
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
            .Where(f => f.EsVigente)
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
                Redondear(delMes.Sum(f => f.PrecioCop ?? 0m), DecimalesMonto),
                Redondear(ganadas.Sum(f => f.PrecioCop ?? 0m), DecimalesMonto)));
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
        var universo = filas.Where(f => f.EsVigente).ToList();
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
                    Redondear(g.Sum(f => f.PrecioCop ?? 0m), DecimalesMonto),
                    Redondear(ganadas.Sum(f => f.PrecioCop ?? 0m), DecimalesMonto));
            })
            .OrderByDescending(c => c.Cantidad)
            .ThenBy(c => c.Ciudad, StringComparer.OrdinalIgnoreCase)];

    private static decimal? Fraccion(int numerador, int denominador) =>
        denominador == 0 ? null : Redondear((decimal)numerador / denominador, DecimalesFraccion);

    private static decimal Redondear(decimal valor, int decimales) => Math.Round(valor, decimales, MidpointRounding.ToEven);
}
