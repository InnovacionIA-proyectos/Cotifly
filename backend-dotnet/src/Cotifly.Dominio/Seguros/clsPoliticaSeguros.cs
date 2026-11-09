namespace Cotifly.Dominio.Seguros;

/// <summary>
/// HU-046.3 (#269): la política de seguros por valor de la mercancía tal como está HOY en sus tablas (<c>db/scripts/21_politica_seguros.sql</c>,
/// HU-046.2 #268): los tramos activos, en orden, cada uno con las medidas activas que aplican y su nivel. Las reglas viven en los datos
/// (decisión 9 a 11 de HU-046.1): el backend no sabe qué medida es un acompañante ni cuál es obligatoria; solo cruza el
/// <c>codigoServicio</c> de los conexos de la ruta contra los de cada medida. Se lee en cada petición (sin caché), así que un cambio en
/// las tablas se ve sin desplegar.
/// </summary>
public sealed class clsPoliticaSeguros
{
    /// <summary>B7 de la auditoría: <c>aviso_codigo</c> de la ruta sin <c>valor_mercancia</c> o con valor 0.</summary>
    public const string CodigoSinValorMercancia = "sin_valor_mercancia";

    /// <summary>B7 de la auditoría: <c>aviso_codigo</c> de un valor que no cae en ningún tramo activo (error de datos).</summary>
    public const string CodigoSinTramo = "sin_tramo";

    /// <summary>Aviso de la ruta sin <c>valor_mercancia</c> o con valor 0 (decisión 4: avisa, no bloquea).</summary>
    public const string AvisoSinValorMercancia =
        "La ruta no tiene valor de la mercancía: no se puede saber qué exige la política de seguros. Registra el valor para ver sus medidas.";

    /// <summary>Aviso de un valor que no cae en ningún tramo activo (la política sin tramos o con un hueco: un error de datos).</summary>
    public const string AvisoSinTramo =
        "La política de seguros no tiene un tramo activo para el valor de la mercancía de esta ruta: revísala con Datos.";

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
    /// Lo que exige la política a una ruta con <paramref name="valorMercancia"/> y los conexos de <paramref name="codigosConexos"/>
    /// (<c>codigoServicio</c> de Nexo; los <c>null</c> no cumplen nada). Una medida obligatoria se cumple con CUALQUIERA de sus conexos;
    /// una obligatoria sin conexos nunca queda pendiente (nada la podría cumplir): se muestra como recomendada.
    /// </summary>
    public clsEvaluacionPoliticaSeguro Evaluar(decimal? valorMercancia, IEnumerable<string?> codigosConexos)
    {
        ArgumentNullException.ThrowIfNull(codigosConexos);
        if (valorMercancia is not { } valor || valor <= 0m)
        {
            return new clsEvaluacionPoliticaSeguro(null, [], [], AvisoSinValorMercancia) { AvisoCodigo = CodigoSinValorMercancia };
        }

        if (TramoDe(valor) is not { } tramo)
        {
            return new clsEvaluacionPoliticaSeguro(null, [], [], AvisoSinTramo) { AvisoCodigo = CodigoSinTramo };
        }

        var presentes = codigosConexos.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recomendadas = new List<clsMedidaRecomendada>();
        var obligatorias = new List<clsMedidaObligatoria>();
        foreach (var medida in tramo.Medidas)
        {
            if (medida.EsObligatoria && medida.Conexos.Count > 0)
            {
                obligatorias.Add(new clsMedidaObligatoria(medida.Codigo, medida.Nombre, medida.Conexos.Any(presentes.Contains), medida.Conexos));
            }
            else
            {
                recomendadas.Add(new clsMedidaRecomendada(medida.Codigo, medida.Nombre));
            }
        }

        return new clsEvaluacionPoliticaSeguro(tramo, recomendadas, obligatorias, null);
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
/// Una medida de un tramo: su <see cref="Codigo"/> (único), el <see cref="Nombre"/> que se muestra, el <see cref="Nivel"/> en ese tramo y los
/// <c>codigoServicio</c> de Nexo que la cumplen (<c>PoliticaSeguroMedidaConexos</c>; vacío = no se cumple con un conexo).
/// </summary>
public sealed record clsMedidaTramoSeguro(string Codigo, string Nombre, string Nivel, IReadOnlyList<string> Conexos)
{
    /// <summary>¿Es obligatoria en el tramo?</summary>
    public bool EsObligatoria => string.Equals(Nivel, clsNivelMedidaSeguro.Obligatoria, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Una medida recomendada en el tramo de la ruta (solo se muestra).</summary>
public sealed record clsMedidaRecomendada(string Codigo, string Nombre);

/// <summary>
/// Una medida obligatoria en el tramo de la ruta: <see cref="Cumplida"/> si la ruta tiene alguno de sus <see cref="Conexos"/> (los códigos
/// que la cumplen, para el botón "agregar").
/// </summary>
public sealed record clsMedidaObligatoria(string Codigo, string Nombre, bool Cumplida, IReadOnlyList<string> Conexos)
{
    public const string EstadoCumplida = "cumplida";
    public const string EstadoPendiente = "pendiente";

    /// <summary><c>cumplida</c> o <c>pendiente</c>.</summary>
    public string Estado => Cumplida ? EstadoCumplida : EstadoPendiente;
}

/// <summary>
/// Lo que la política exige a una ruta: su <see cref="Tramo"/> (<c>null</c> sin valor o sin tramo), las medidas recomendadas, las
/// obligatorias con su estado y un <see cref="Aviso"/> cuando no se pudo ubicar el tramo.
/// </summary>
public sealed record clsEvaluacionPoliticaSeguro(
    clsTramoPoliticaSeguro? Tramo, IReadOnlyList<clsMedidaRecomendada> Recomendadas, IReadOnlyList<clsMedidaObligatoria> Obligatorias, string? Aviso)
{
    /// <summary>
    /// B7 de la auditoría: el código estable del <see cref="Aviso"/> (<see cref="clsPoliticaSeguros.CodigoSinValorMercancia"/> o
    /// <see cref="clsPoliticaSeguros.CodigoSinTramo"/>), para que el frontend no compare textos; <c>null</c> sin aviso.
    /// </summary>
    public string? AvisoCodigo { get; init; }

    /// <summary>Las obligatorias pendientes (bloquean "Finalizar" sin justificación).</summary>
    public IReadOnlyList<clsMedidaObligatoria> Pendientes => [.. Obligatorias.Where(m => !m.Cumplida)];
}

/// <summary>Las medidas obligatorias pendientes de una ruta cotizable (el detalle de <c>medidas_obligatorias_faltantes</c>).</summary>
public sealed record clsMedidasFaltantesRuta(int LineaOrden, Guid LineaSolicitudId, IReadOnlyList<clsMedidaObligatoria> Medidas);
