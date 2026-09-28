using System.Globalization;
using System.Text.Json;

namespace Cotifly.Aplicacion.Agente.Herramientas;

/// <summary>
/// Claves de los argumentos que inyecta el orquestador (<c>clsInyeccionEstadoFilter</c>, ADR-004 §4) en <c>KernelArguments</c> y
/// lectura tolerante de los valores (el proveedor entrega <see cref="JsonElement"/>; el filtro, tipos .NET). Los argumentos de
/// identidad no son parámetros de las herramientas: el modelo no los ve en el esquema y, si los manda, el filtro los sobrescribe.
/// </summary>
public static class clsArgumentosHerramienta
{
    public const string UsuarioId = "usuarioId";
    public const string SpaceId = "spaceId";
    public const string ThreadId = "threadId";
    public const string NitCliente = "nitCliente";
    public const string OrigenCliente = "origenCliente";
    public const string BpCliente = "bpCliente";
    public const string LineaNegocioId = "lineaNegocioId";
    public const string CotizacionId = "cotizacionId";
    public const string Folio = "folio";
    public const string MargenPolitica = "margenPolitica";
    public const string Costo = "costo";
    public const string Margen = "margen";
    public const string Metodo = "metodo";
    public const string Cliente = "cliente";
    public const string Cotizacion = "cotizacion";
    public const string Origen = "origen";
    public const string Destino = "destino";
    public const string TextoLibre = "texto";
    public const string Campo = "campo";

    /// <summary>Texto del argumento (sin espacios sobrantes), o <c>null</c> si falta o está vacío.</summary>
    public static string? Texto(IReadOnlyDictionary<string, object?> argumentos, string clave)
    {
        ArgumentNullException.ThrowIfNull(argumentos);
        return Buscar(argumentos, clave) switch
        {
            null => null,
            string texto => Limpio(texto),
            JsonElement { ValueKind: JsonValueKind.String } elemento => Limpio(elemento.GetString()),
            JsonElement { ValueKind: JsonValueKind.Number } elemento => elemento.GetRawText(),
            JsonElement => null,
            IFormattable formateable => formateable.ToString(null, CultureInfo.InvariantCulture),
            var otro => Limpio(otro.ToString()),
        };
    }

    /// <summary>Número del argumento (acepta número JSON o texto con punto decimal), o <c>null</c>.</summary>
    public static decimal? Decimal(IReadOnlyDictionary<string, object?> argumentos, string clave)
    {
        ArgumentNullException.ThrowIfNull(argumentos);
        return Buscar(argumentos, clave) switch
        {
            decimal numero => numero,
            double numero => (decimal)numero,
            int numero => numero,
            long numero => numero,
            JsonElement { ValueKind: JsonValueKind.Number } elemento when elemento.TryGetDecimal(out var numero) => numero,
            JsonElement { ValueKind: JsonValueKind.String } elemento => Parsear(elemento.GetString()),
            string texto => Parsear(texto),
            _ => null,
        };
    }

    /// <summary>Identificador (UUID) del argumento, o <c>null</c>.</summary>
    public static Guid? Identificador(IReadOnlyDictionary<string, object?> argumentos, string clave) =>
        Buscar(argumentos, clave) is Guid id ? id : (Guid.TryParse(Texto(argumentos, clave), out var otro) ? otro : null);

    /// <summary>Lista de textos del argumento (arreglo JSON, lista .NET o un texto suelto).</summary>
    public static IReadOnlyList<string> Lista(IReadOnlyDictionary<string, object?> argumentos, string clave)
    {
        ArgumentNullException.ThrowIfNull(argumentos);
        return Buscar(argumentos, clave) switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } arreglo => arreglo.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => Limpio(e.GetString()))
                .OfType<string>()
                .ToList(),
            IEnumerable<string> lista => lista.Select(Limpio).OfType<string>().ToList(),
            JsonElement { ValueKind: JsonValueKind.String } elemento when Limpio(elemento.GetString()) is { } unico => [unico],
            string texto when Limpio(texto) is { } unico => [unico],
            _ => [],
        };
    }

    private static object? Buscar(IReadOnlyDictionary<string, object?> argumentos, string clave)
    {
        if (argumentos.TryGetValue(clave, out var valor))
        {
            return valor;
        }

        foreach (var (nombre, candidato) in argumentos)
        {
            if (string.Equals(nombre, clave, StringComparison.OrdinalIgnoreCase))
            {
                return candidato;
            }
        }

        return null;
    }

    private static string? Limpio(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : texto.Trim();

    private static decimal? Parsear(string? texto) =>
        decimal.TryParse(texto?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var numero) ? numero : null;
}
