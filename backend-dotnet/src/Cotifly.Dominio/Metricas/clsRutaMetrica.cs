namespace Cotifly.Dominio.Metricas;

/// <summary>
/// Una ruta (<c>CotizacionLineasSolicitud</c>) de la versión vigente de una cotización del tablero (HU-021.10, decisión 8): los valores
/// y la apertura por línea de negocio son por ruta; los conteos siguen siendo por cotización (<see cref="clsCotizacionMetrica"/>).
/// </summary>
public sealed record clsRutaMetrica
{
    /// <summary>Posición de la ruta dentro de la solicitud (1, 2, 3…).</summary>
    public int Orden { get; init; }

    /// <summary><c>MargenesPolitica.id</c> de la ruta; <c>null</c> si la ruta no tiene línea de negocio.</summary>
    public int? LineaNegocioId { get; init; }

    /// <summary><c>MargenesPolitica.lineaNegocio</c>.</summary>
    public string? LineaNegocio { get; init; }

    /// <summary><c>costoFinal</c> de la ruta en COP (fletes + retorno + levante + conexos; nunca se convierte).</summary>
    public decimal? Costo { get; init; }

    /// <summary><c>precioFinalLinea</c> en la moneda de su cotización.</summary>
    public decimal? PrecioFinal { get; init; }

    public decimal? MargenAplicado { get; init; }

    /// <summary>
    /// HU-016.1 (#154): <c>margenPoliticaSugerido</c> de la ruta, el margen de política de su línea guardado al cotizar; <c>null</c> en
    /// datos anteriores a HU-021. Es la referencia con que se compara el margen aplicado en el tablero.
    /// </summary>
    public decimal? MargenPolitica { get; init; }

    /// <summary>
    /// <c>MargenAjustes</c> de la ruta (<c>lineaSolicitudId</c>). Un ajuste de la cotización sin ruta (o con una ruta que no es suya)
    /// cuenta en la ruta 1, igual que la migración del 0009: así la suma de las rutas da los ajustes de la cotización.
    /// </summary>
    public int NumAjustes { get; init; }

    /// <summary>Ajustes de la ruta con <c>diferencia</c> no nula.</summary>
    public int NumDiferencias { get; init; }

    /// <summary>Suma de <c>MargenAjustes.diferencia</c> de la ruta.</summary>
    public decimal? SumaDiferencias { get; init; }
}
