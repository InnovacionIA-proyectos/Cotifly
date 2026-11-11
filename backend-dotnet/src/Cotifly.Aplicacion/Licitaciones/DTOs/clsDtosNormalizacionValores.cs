namespace Cotifly.Aplicacion.Licitaciones.DTOs;

// HU-044.4 (#255, ADR-029): POST /web/licitaciones/normalizar-valores. Lo que llega del front y lo que vuelve, ya en los nombres de
// Cotifly (la Api los escribe en snake_case). La forma de NEXO (camelCase, campo tipoVehiculo/lineaNegocio) la arma el adaptador.

/// <summary>
/// Entrada del caso de uso: el NIT de la licitación (opcional; con él NEXO usa además el diccionario de ese cliente) y los textos
/// distintos del Excel. <c>null</c> en la lista es un valor inválido (lo reporta la validación con su posición).
/// </summary>
public sealed record clsEntradaNormalizacion(string? NitCliente, IReadOnlyList<clsValorEntradaNormalizacion?>? Valores);

/// <summary>
/// Un texto del Excel: <paramref name="Campo"/> = <c>tipo_vehiculo</c> | <c>carroceria</c> | <c>ciudad</c> | <c>linea_negocio</c>
/// (<see cref="clsCamposNormalizacion"/>) y el <paramref name="Valor"/> tal como viene en la celda.
/// </summary>
public sealed record clsValorEntradaNormalizacion(string? Campo, string? Valor);

/// <summary>
/// Respuesta: si NEXO usó IA, el motivo si no (<c>SIN_PENDIENTES</c>, <c>IA_NO_DISPONIBLE</c> o un <c>IA_*</c> del proveedor) y un
/// resultado por cada valor distinto de la petición, en el mismo orden.
/// </summary>
public sealed record clsNormalizacionValores(bool IaUsada, string? MotivoSinIa, IReadOnlyList<clsValorNormalizacionWeb> Valores);

/// <summary>
/// Un valor ya normalizado. <paramref name="Estado"/> = <c>resuelto</c> | <c>ambiguo</c> | <c>sin_resolver</c>; con
/// <paramref name="Origen"/> = <c>ia</c> es una propuesta que el comercial confirma. <paramref name="MunicipioId"/> solo en una ciudad
/// resuelta (el id de municipio de NEXO, el mismo de <c>GET /web/municipios</c>); <paramref name="Candidatos"/> solo en una ciudad ambigua.
/// En <c>linea_negocio</c>, <paramref name="Codigo"/> es la línea de negocio de COTIFLY (la que va en <c>lineas[].linea_negocio</c> de
/// <c>POST /web/licitaciones</c>), <paramref name="LineaNexo"/> la de NEXO de la que sale y, si dos líneas de Cotifly comparten esa línea de
/// NEXO, <paramref name="LineasCandidatas"/> trae sus nombres (estado <c>ambiguo</c>).
/// </summary>
public sealed record clsValorNormalizacionWeb(
    string Campo,
    string Valor,
    string Estado,
    string? Codigo,
    string? Descripcion,
    int? MunicipioId,
    string Origen,
    double Confianza,
    string? Nota,
    IReadOnlyList<clsCandidatoCiudad>? Candidatos,
    string? LineaNexo,
    IReadOnlyList<string>? LineasCandidatas);

/// <summary>Los campos que se normalizan, con su nombre en Cotifly (snake_case) y en NEXO (camelCase).</summary>
public static class clsCamposNormalizacion
{
    public const string TipoVehiculo = "tipo_vehiculo";
    public const string Carroceria = "carroceria";
    public const string Ciudad = "ciudad";
    public const string LineaNegocio = "linea_negocio";

    /// <summary>Estados de un valor (los de NEXO).</summary>
    public const string Resuelto = "resuelto";
    public const string Ambiguo = "ambiguo";
    public const string SinResolver = "sin_resolver";

    /// <summary>Origen de una línea de negocio que Cotifly reconoció sin NEXO (el texto es el nombre de una línea de Cotifly).</summary>
    public const string OrigenLineaCotifly = "linea_cotifly";

    /// <summary>Origen de un valor sin resolver: el de NEXO cuando nada lo reconoce.</summary>
    public const string OrigenNinguno = "ninguno";

    /// <summary><c>motivoSinIa</c> de NEXO cuando las reglas resolvieron todo; Cotifly lo usa igual si no tuvo que llamar a NEXO.</summary>
    public const string MotivoSinPendientes = "SIN_PENDIENTES";

    private static readonly Dictionary<string, string> _aNexo = new(StringComparer.Ordinal)
    {
        [TipoVehiculo] = "tipoVehiculo", [Carroceria] = "carroceria", [Ciudad] = "ciudad", [LineaNegocio] = "lineaNegocio",
    };

    private static readonly Dictionary<string, string> _deNexo = _aNexo.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Los cuatro campos, en el orden del contrato.</summary>
    public static IReadOnlyList<string> Todos { get; } = [TipoVehiculo, Carroceria, Ciudad, LineaNegocio];

    /// <summary>El nombre del campo en NEXO; <c>null</c> si <paramref name="campo"/> no es uno de los cuatro.</summary>
    public static string? ANexo(string? campo) => campo is null ? null : _aNexo.GetValueOrDefault(campo);

    /// <summary>El nombre en Cotifly de un campo de NEXO; <c>null</c> si no es uno de los cuatro.</summary>
    public static string? DeNexo(string? campoNexo) => campoNexo is null ? null : _deNexo.GetValueOrDefault(campoNexo);
}
