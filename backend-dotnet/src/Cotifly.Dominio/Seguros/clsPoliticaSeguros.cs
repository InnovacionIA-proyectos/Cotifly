namespace Cotifly.Dominio.Seguros;

/// <summary>
/// La política de seguros por valor de la mercancía tal como está en sus tablas (<c>db/scripts/21_politica_seguros.sql</c>):
/// los tramos activos, en orden, cada uno con las medidas activas que aplican y su nivel. Las reglas viven en los datos: el backend no sabe qué medida es un acompañante ni cuál es obligatoria; solo cruza el
/// <c>codigoServicio</c> de los conexos de la ruta contra los de cada medida. Se lee en cada petición (sin caché), así que un cambio en
/// las tablas se ve sin desplegar.
/// </summary>
public sealed class clsPoliticaSeguros
{
    /// <summary>
    /// Código de la medida "incluir el seguro de la mercancía": se evalúa por su código, no por
    /// conexos — <c>cumplida</c> si la ruta ya incluye el seguro, si no <c>pendiente</c>.
    /// </summary>
    public const string CodigoIncluirSeguro = "incluir_seguro";

    /// <summary>
    /// Valor del campo <c>aviso</c> del bloque cuando la ruta no tiene <c>valor_mercancia</c> o lo tiene en 0
    /// (<c>aviso: "SIN_VALOR_MERCANCIA" | null</c>). Avisa, no bloquea.
    /// </summary>
    public const string AvisoSinValorMercancia = "SIN_VALOR_MERCANCIA";

    /// <summary>La política sin tramos (tablas vacías): ninguna ruta cae en un tramo.</summary>
    public static readonly clsPoliticaSeguros Vacia = new([]);

    public clsPoliticaSeguros(IEnumerable<clsTramoPoliticaSeguro> tramos)
    {
        ArgumentNullException.ThrowIfNull(tramos);
        Tramos = [.. tramos.OrderBy(t => t.ValorDesde)];
    }

    /// <summary>Los tramos activos, por <see cref="clsTramoPoliticaSeguro.ValorDesde"/>.</summary>
    public IReadOnlyList<clsTramoPoliticaSeguro> Tramos { get; }

    /// <summary>
    /// El tramo del valor total de la mercancía de la ruta (decisión 2: por ruta, nunca por camión): <c>valorDesde</c> exclusivo y
    /// <c>valorHasta</c> incluido (decisión 3). <c>null</c> sin valor, con valor 0 o negativo (como sin valor) o si ningún tramo lo contiene.
    /// </summary>
    public clsTramoPoliticaSeguro? TramoDe(decimal? valorMercancia) =>
        valorMercancia is { } valor && valor > 0m ? Tramos.FirstOrDefault(t => t.Contiene(valor)) : null;

    /// <summary>
    /// Lo que exige la política a una ruta con <paramref name="valorMercancia"/>, si la ruta <paramref name="incluyeSeguro"/> y los conexos
    /// de <paramref name="codigosConexos"/> (<c>codigoServicio</c> de Nexo; los <c>null</c> no cumplen nada). Las medidas se parten por su
    /// NIVEL (<c>recomendada</c> | <c>obligatoria</c>) y cada una trae su estado:
    /// <list type="bullet">
    /// <item><c>incluir_seguro</c> (por código): <c>cumplida</c> si la ruta incluye el seguro, si no <c>pendiente</c>.</item>
    /// <item>medida con conexos: <c>cumplida</c> si la ruta tiene alguno (sin distinguir mayúsculas), si no <c>pendiente</c>.</item>
    /// <item>medida sin conexos: <c>sin_verificar</c>, que nunca bloquea.</item>
    /// </list>
    /// Solo una obligatoria <c>pendiente</c> bloquea "Finalizar".
    /// </summary>
    public clsEvaluacionPoliticaSeguro Evaluar(decimal? valorMercancia, bool incluyeSeguro, IEnumerable<string?> codigosConexos)
    {
        ArgumentNullException.ThrowIfNull(codigosConexos);
        if (valorMercancia is not { } valor || valor <= 0m)
        {
            return new clsEvaluacionPoliticaSeguro(null, [], [], AvisoSinValorMercancia);
        }

        if (TramoDe(valor) is not { } tramo)
        {
            // Valor positivo que no cae en ningún tramo activo: un hueco en los datos de la política. No se exige nada y no hay aviso
            // de contrato (el front solo conoce SIN_VALOR_MERCANCIA | null); Datos lo corrige en las tablas.
            return new clsEvaluacionPoliticaSeguro(null, [], [], null);
        }

        var presentes = codigosConexos.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recomendadas = new List<clsMedidaEvaluada>();
        var obligatorias = new List<clsMedidaEvaluada>();
        foreach (var medida in tramo.Medidas)
        {
            var evaluada = new clsMedidaEvaluada(
                medida.Codigo, medida.Nombre, medida.EsCobrable, EstadoDe(medida, incluyeSeguro, presentes), medida.Conexos);
            if (medida.EsObligatoria)
            {
                obligatorias.Add(evaluada);
            }
            else
            {
                recomendadas.Add(evaluada);
            }
        }

        return new clsEvaluacionPoliticaSeguro(tramo, recomendadas, obligatorias, null);
    }

    /// <summary>El estado de una medida en la ruta (ver <see cref="Evaluar"/>).</summary>
    private static string EstadoDe(clsMedidaTramoSeguro medida, bool incluyeSeguro, IReadOnlySet<string> presentes)
    {
        if (string.Equals(medida.Codigo, CodigoIncluirSeguro, StringComparison.OrdinalIgnoreCase))
        {
            return incluyeSeguro ? clsMedidaEvaluada.EstadoCumplida : clsMedidaEvaluada.EstadoPendiente;
        }

        if (medida.Conexos.Count == 0)
        {
            return clsMedidaEvaluada.EstadoSinVerificar;
        }

        return medida.Conexos.Any(presentes.Contains) ? clsMedidaEvaluada.EstadoCumplida : clsMedidaEvaluada.EstadoPendiente;
    }
}

/// <summary>Niveles de <c>PoliticaSeguroTramoMedidas.nivel</c> (CHECK <c>ck_politicaSeguroTramoMedidas_nivel</c>).</summary>
public static class clsNivelMedidaSeguro
{
    public const string Recomendada = "recomendada";
    public const string Obligatoria = "obligatoria";
}

/// <summary>
/// Un tramo activo de <c>PoliticaSeguroTramos</c>: (<see cref="ValorDesde"/>, <see cref="ValorHasta"/>] en pesos fijos; <see cref="ValorHasta"/>
/// <c>null</c> = sin tope. <see cref="Medidas"/> son las activas que aplican en el tramo (<c>PoliticaSeguroTramoMedidas</c>).
/// </summary>
public sealed record clsTramoPoliticaSeguro(
    Guid Id, string Nombre, decimal ValorDesde, decimal? ValorHasta, IReadOnlyList<clsMedidaTramoSeguro> Medidas)
{
    /// <summary>¿<paramref name="valor"/> cae en el tramo? <c>valorDesde</c> exclusivo, <c>valorHasta</c> incluido.</summary>
    public bool Contiene(decimal valor) => valor > ValorDesde && (ValorHasta is not { } hasta || valor <= hasta);
}

/// <summary>
/// Una medida de un tramo: su <see cref="Codigo"/> (único), el <see cref="Nombre"/> que se muestra, si <see cref="EsCobrable"/>, el
/// <see cref="Nivel"/> en ese tramo y los <c>codigoServicio</c> de Nexo que la cumplen (<c>PoliticaSeguroMedidaConexos</c>; vacío = no se
/// cumple con un conexo).
/// </summary>
public sealed record clsMedidaTramoSeguro(string Codigo, string Nombre, bool EsCobrable, string Nivel, IReadOnlyList<string> Conexos)
{
    /// <summary>¿Es obligatoria en el tramo?</summary>
    public bool EsObligatoria => string.Equals(Nivel, clsNivelMedidaSeguro.Obligatoria, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Una medida evaluada en el tramo de la ruta (contrato de la API, <c>Medida = {codigo, nombre, es_cobrable, estado, conexos[]}</c>): su
/// <see cref="Estado"/> (<c>cumplida</c> | <c>pendiente</c> | <c>sin_verificar</c>) y los <c>codigoServicio</c> de Nexo que la cumplen
/// (<see cref="Conexos"/>, para el botón "agregar"). Se usa igual en las recomendadas y en las obligatorias.
/// </summary>
public sealed record clsMedidaEvaluada(string Codigo, string Nombre, bool EsCobrable, string Estado, IReadOnlyList<string> Conexos)
{
    public const string EstadoCumplida = "cumplida";
    public const string EstadoPendiente = "pendiente";

    /// <summary>La medida sin conexos que no se puede verificar (nunca bloquea).</summary>
    public const string EstadoSinVerificar = "sin_verificar";

    /// <summary>¿La medida está cumplida?</summary>
    public bool Cumplida => string.Equals(Estado, EstadoCumplida, StringComparison.Ordinal);

    /// <summary>¿La medida está pendiente? (Solo una obligatoria pendiente bloquea "Finalizar".)</summary>
    public bool Pendiente => string.Equals(Estado, EstadoPendiente, StringComparison.Ordinal);
}

/// <summary>
/// Lo que la política exige a una ruta: su <see cref="Tramo"/> (<c>null</c> sin valor o sin tramo), las medidas recomendadas y las
/// obligatorias (ambas con su estado) y un <see cref="Aviso"/> (<c>"SIN_VALOR_MERCANCIA"</c> o <c>null</c>).
/// </summary>
public sealed record clsEvaluacionPoliticaSeguro(
    clsTramoPoliticaSeguro? Tramo, IReadOnlyList<clsMedidaEvaluada> Recomendadas, IReadOnlyList<clsMedidaEvaluada> Obligatorias, string? Aviso)
{
    /// <summary>Las obligatorias pendientes (bloquean "Finalizar" sin justificación). Una <c>sin_verificar</c> no cuenta.</summary>
    public IReadOnlyList<clsMedidaEvaluada> Pendientes => [.. Obligatorias.Where(m => m.Pendiente)];
}

/// <summary>Las medidas obligatorias pendientes de una ruta cotizable (el detalle de <c>medidas_obligatorias_faltantes</c>).</summary>
public sealed record clsMedidasFaltantesRuta(
    int LineaOrden, string? Origen, string? Destino, IReadOnlyList<clsMedidaEvaluada> Medidas);
