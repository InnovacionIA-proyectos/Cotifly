using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Cotifly.Aplicacion.CatalogosFlete.DTOs;

/// <summary>Los dos tipos de elemento del diccionario de alias de Nexo: tipo de vehículo y clase de contenedor.</summary>
public enum TipoAliasNexo
{
    /// <summary><c>tipo=vehiculo</c> (Nexo lo guarda como <c>Vehiculo</c>): "TB", "T.B", "Turbo" → <c>2L2</c>.</summary>
    Vehiculo,

    /// <summary><c>tipo=contenedor</c> (Nexo lo guarda como <c>Contenedor</c>): "40 HC", "40HC" → <c>CNT40.HC</c>.</summary>
    Contenedor,
}

/// <summary>
/// El diccionario de alias de Nexo (<c>tbl_alias_elementos</c>) descargado con
/// <c>GET /api/catalogos/alias</c>: por tipo, el texto normalizado → el código oficial. Solo trae los alias activos.
/// </summary>
/// <remarks>
/// <para><b>Por qué el diccionario completo y no <c>GET /api/catalogos/alias/resolver</c> texto por texto.</b> El diccionario es pequeño
/// (decenas de filas), cambia poco y no es sensible: una sola petición cada <c>Nexo:MinutosCacheCatalogos</c> sirve para todas las
/// líneas de un envío y para todas las filas de una licitación (el resolver pediría una petición por texto, y el límite de Nexo,
/// <c>listados-estandar</c>, es de 120 por minuto POR ROL: todos los comerciales de Cotifly comparten ese cupo). Con Nexo caído se
/// sigue usando la última copia (hasta <c>Nexo:HorasCatalogosVencidos</c>), lo que el resolver no permite.</para>
/// <para><b>Mismo resultado que el resolver de Nexo.</b> La clave es <c>aliasNormalizado</c> tal como lo calculó Nexo, y el texto
/// buscado se normaliza con <see cref="Normalizar"/>, copia exacta de <c>clsResolucionDeCatalogos.Normalizar</c> de Nexo (espacios
/// colapsados, sin espacio antes de <c>.</c> o <c>,</c>, <c>_</c> y <c>-</c> como espacio, sin tildes, en mayúsculas). Igual que en
/// Nexo, un alias inactivo o de otro tipo no resuelve.</para>
/// </remarks>
public sealed class clsDiccionarioAliasNexo
{
    private readonly Dictionary<string, string> _vehiculos;
    private readonly Dictionary<string, string> _contenedores;

    /// <summary>Diccionario sin alias.</summary>
    public static clsDiccionarioAliasNexo Vacio { get; } = new([]);

    /// <summary>
    /// Arma el diccionario con las filas de Nexo. Una fila sin texto o sin código se ignora; si dos filas dan la misma clave en un tipo, gana
    /// la primera (en Nexo <c>aliasNormalizado</c> es único en toda la tabla, así que no debería pasar).
    /// </summary>
    public clsDiccionarioAliasNexo(IEnumerable<clsFilaAliasNexo> filas)
    {
        ArgumentNullException.ThrowIfNull(filas);
        _vehiculos = new Dictionary<string, string>(StringComparer.Ordinal);
        _contenedores = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fila in filas)
        {
            if (fila is null || string.IsNullOrWhiteSpace(fila.Codigo))
            {
                continue;
            }

            var destino = fila.Tipo == TipoAliasNexo.Vehiculo ? _vehiculos : _contenedores;
            foreach (var clave in new[] { fila.AliasNormalizado, Normalizar(fila.Alias) })
            {
                if (!string.IsNullOrEmpty(clave))
                {
                    destino.TryAdd(clave, fila.Codigo.Trim());
                }
            }
        }
    }

    /// <summary>Cantidad de claves de cada tipo (para el log).</summary>
    public int Cantidad(TipoAliasNexo tipo) => (tipo == TipoAliasNexo.Vehiculo ? _vehiculos : _contenedores).Count;

    /// <summary>El código oficial al que apunta <paramref name="texto"/> en el diccionario de <paramref name="tipo"/>, o <c>null</c>.</summary>
    public string? Resolver(TipoAliasNexo tipo, string? texto)
    {
        var clave = Normalizar(texto);
        if (clave.Length == 0)
        {
            return null;
        }

        return (tipo == TipoAliasNexo.Vehiculo ? _vehiculos : _contenedores).TryGetValue(clave, out var codigo) ? codigo : null;
    }

    /// <summary>
    /// Copia de <c>clsResolucionDeCatalogos.Normalizar</c> de Nexo: la clave con la que Nexo compara un
    /// texto contra el diccionario. Vacío si no hay texto.
    /// </summary>
    public static string Normalizar(string? texto)
    {
        var limpio = Limpiar(texto);
        if (limpio is null)
        {
            return string.Empty;
        }

        var sinSeparadores = limpio.Replace('_', ' ').Replace('-', ' ');
        return (Limpiar(SinTildes(sinSeparadores)) ?? string.Empty).ToUpperInvariant();
    }

    /// <summary><c>vehiculo</c>/<c>vehículo</c> o <c>contenedor</c> (sin distinguir mayúsculas), como <c>clsAliasElemento.TryParseTipo</c> de Nexo.</summary>
    public static bool TryParseTipo(string? texto, out TipoAliasNexo tipo)
    {
        tipo = TipoAliasNexo.Vehiculo;
        if (string.IsNullOrWhiteSpace(texto))
        {
            return false;
        }

        var t = texto.Trim();
        if (t.Equals("vehiculo", StringComparison.OrdinalIgnoreCase) || t.Equals("vehículo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (t.Equals("contenedor", StringComparison.OrdinalIgnoreCase))
        {
            tipo = TipoAliasNexo.Contenedor;
            return true;
        }

        return false;
    }

    private static string? Limpiar(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            return null;
        }

        var colapsado = string.Join(' ', texto.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return Regex.Replace(colapsado, @"\s+([.,])", "$1", RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static string SinTildes(string texto)
    {
        var descompuesto = texto.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(descompuesto.Length);
        foreach (var c in descompuesto)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>
/// Una fila activa de <c>GET /api/catalogos/alias</c> de Nexo (<c>clsAliasDto</c>: <c>alias</c>, <c>aliasNormalizado</c>,
/// <c>tipoElemento</c>, <c>codigo</c>, <c>activo</c>).
/// </summary>
public sealed record clsFilaAliasNexo(TipoAliasNexo Tipo, string Alias, string? AliasNormalizado, string Codigo);
