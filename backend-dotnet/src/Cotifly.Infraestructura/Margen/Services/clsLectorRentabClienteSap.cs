using System.Globalization;
using System.Text.Json;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>Una combinación de <c>RentabClienteSet</c> lista para <c>#SapMargenStage</c> (HU-042.2, #243).</summary>
/// <param name="SalesOrgId">Organización de ventas, recortada y en mayúsculas.</param>
/// <param name="TrqType">Tipo de solicitud de SAP, recortado y en mayúsculas.</param>
/// <param name="BpSap">BP del cliente recortado, tal como lo manda SAP (con sus ceros: el consumo los normaliza al buscar).</param>
/// <param name="Margen">Porcentaje (13.5 = 13,5 %) redondeado a 4 decimales, como <c>decimal(9,4)</c>.</param>
public sealed record clsFilaMargenSap(string SalesOrgId, string TrqType, string BpSap, decimal Margen);

/// <summary>Una respuesta de SAP o del DW que no se puede sincronizar. El mensaje va a <c>SapMargenSyncLog</c>: nunca lleva URL ni credenciales.</summary>
public sealed class clsSincronizacionMargenSapException : Exception
{
    public clsSincronizacionMargenSapException()
    {
    }

    public clsSincronizacionMargenSapException(string message)
        : base(message)
    {
    }

    public clsSincronizacionMargenSapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Lectura del set completo de <c>RentabClienteSet</c> de SAP TM (OData v2: <c>{"d":{"results":[{"__metadata":{...}, "SalesOrgId",
/// "TrqType", "OrderPartyId", "Value"}]}}</c>), HU-042.2 (#243). Todo o nada: una sola fila inválida descarta el set (la tabla no se toca)
/// con <see cref="clsSincronizacionMargenSapException"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>SalesOrgId</c>, <c>TrqType</c> y <c>OrderPartyId</c>: texto no vacío dentro del largo de su columna (20, 8 y 20).</item>
/// <item><c>Value</c>: texto (<c>Edm.Decimal</c> de OData v2, con artefactos de flotante como <c>"13.592356781"</c>) o número, en cultura
/// invariante; se redondea a 4 decimales (alejándose de cero, como SQL Server) y debe caber en <c>decimal(9,4)</c>.</item>
/// <item>Una clave (organización, TrqType, BP) repetida es un error: la PK de <c>#SapMargenStage</c> la rechazaría. Se compara sin
/// distinguir mayúsculas, como la collation de la base.</item>
/// <item>Un set vacío también es un error (como en el script de Datos): sin él, el stored procedure desactivaría todo.</item>
/// </list>
/// </remarks>
public static class clsLectorRentabClienteSap
{
    public const string CampoSalesOrg = "SalesOrgId";
    public const string CampoTrqType = "TrqType";
    public const string CampoOrderParty = "OrderPartyId";
    public const string CampoValor = "Value";

    /// <summary>Largos de las columnas de <c>SapMargenCliente</c> (script 17).</summary>
    public const int LargoSalesOrg = 20;
    public const int LargoTrqType = 8;
    public const int LargoBp = 20;

    /// <summary>Máximo de <c>decimal(9,4)</c>.</summary>
    public const decimal MargenMaximo = 99_999.9999m;

    /// <summary>Compara claves (organización, TrqType, BP) sin distinguir mayúsculas, como la collation de la base.</summary>
    public static IEqualityComparer<(string, string, string)> ComparadorClave { get; } = clsComparadorClave.Instancia;

    /// <summary>Lee el JSON de <paramref name="flujo"/> (ya descomprimido y acotado por quien llama).</summary>
    public static async Task<IReadOnlyList<clsFilaMargenSap>> LeerAsync(Stream flujo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(flujo);
        JsonDocument documento;
        try
        {
            documento = await JsonDocument.ParseAsync(flujo, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new clsSincronizacionMargenSapException("SAP TM respondió un JSON inválido.", ex);
        }

        using (documento)
        {
            return Leer(documento.RootElement);
        }
    }

    /// <summary>Filas validadas y normalizadas de la raíz OData.</summary>
    public static IReadOnlyList<clsFilaMargenSap> Leer(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Object
            || !raiz.TryGetProperty("d", out var d)
            || d.ValueKind != JsonValueKind.Object
            || !d.TryGetProperty("results", out var resultados)
            || resultados.ValueKind != JsonValueKind.Array)
        {
            throw new clsSincronizacionMargenSapException("La respuesta de SAP TM no tiene la forma {\"d\":{\"results\":[...]}}.");
        }

        var filas = new List<clsFilaMargenSap>(resultados.GetArrayLength());
        var claves = new HashSet<(string, string, string)>(ComparadorClave);
        var numero = 0;
        foreach (var fila in resultados.EnumerateArray())
        {
            numero++;
            if (fila.ValueKind != JsonValueKind.Object)
            {
                throw Invalida(numero, "no es un objeto");
            }

            var salesOrg = Texto(fila, CampoSalesOrg, LargoSalesOrg, numero).ToUpperInvariant();
            var trqType = Texto(fila, CampoTrqType, LargoTrqType, numero).ToUpperInvariant();
            var bp = Texto(fila, CampoOrderParty, LargoBp, numero);
            var margen = Valor(fila, numero);
            if (!claves.Add((salesOrg, trqType, bp)))
            {
                throw new clsSincronizacionMargenSapException(string.Create(CultureInfo.InvariantCulture,
                    $"SAP TM repitió la combinación {CampoSalesOrg}={salesOrg}, {CampoTrqType}={trqType}, {CampoOrderParty}={bp} (fila {numero}): no se sincroniza un set con claves duplicadas."));
            }

            filas.Add(new clsFilaMargenSap(salesOrg, trqType, bp, margen));
        }

        if (filas.Count == 0)
        {
            throw new clsSincronizacionMargenSapException("SAP TM devolvió 0 registros: no se sincroniza nada.");
        }

        return filas;
    }

    private static string Texto(JsonElement fila, string campo, int largo, int numero)
    {
        if (!fila.TryGetProperty(campo, out var valor) || valor.ValueKind != JsonValueKind.String)
        {
            throw Invalida(numero, $"no trae '{campo}' de texto");
        }

        var texto = valor.GetString()!.Trim();
        if (texto.Length == 0)
        {
            throw Invalida(numero, $"trae '{campo}' vacío");
        }

        return texto.Length <= largo
            ? texto
            : throw Invalida(numero, string.Create(CultureInfo.InvariantCulture, $"trae '{campo}' de más de {largo} caracteres"));
    }

    private static decimal Valor(JsonElement fila, int numero)
    {
        if (!fila.TryGetProperty(CampoValor, out var valor))
        {
            throw Invalida(numero, $"no trae '{CampoValor}'");
        }

        decimal crudo = valor.ValueKind switch
        {
            JsonValueKind.String when decimal.TryParse(valor.GetString()!.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
            JsonValueKind.Number when valor.TryGetDecimal(out var n) => n,
            _ => throw Invalida(numero, $"trae '{CampoValor}' no numérico"),
        };

        var redondeado = Math.Round(crudo, 4, MidpointRounding.AwayFromZero);
        return Math.Abs(redondeado) <= MargenMaximo
            ? redondeado
            : throw Invalida(numero, $"trae '{CampoValor}' fuera del rango de decimal(9,4)");
    }

    private static clsSincronizacionMargenSapException Invalida(int numero, string motivo) =>
        new(string.Create(CultureInfo.InvariantCulture, $"La fila {numero} de SAP TM {motivo}: no se sincroniza el set."));

    private sealed class clsComparadorClave : IEqualityComparer<(string, string, string)>
    {
        public static readonly clsComparadorClave Instancia = new();

        public bool Equals((string, string, string) x, (string, string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Item3, y.Item3);

        public int GetHashCode((string, string, string) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item1),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item3));
    }
}

/// <summary>
/// Guarda de mínimo de filas (HU-042.2, #243): un set parcial de SAP no debe desactivar al resto. El stored procedure solo se llama si SAP
/// trajo al menos <c>max(SapTm:MinimoFilas, ceil(SapTm:MinimoPorcentajeActivas % × filas activas actuales))</c>.
/// </summary>
public static class clsGuardaMinimoFilasSap
{
    /// <summary>Filas mínimas exigidas.</summary>
    public static int MinimoExigido(int minimoFilas, int porcentajeActivas, int filasActivas)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimoFilas);
        ArgumentOutOfRangeException.ThrowIfNegative(porcentajeActivas);
        ArgumentOutOfRangeException.ThrowIfNegative(filasActivas);
        var relativo = (int)(((long)porcentajeActivas * filasActivas + 99) / 100);
        return Math.Max(minimoFilas, relativo);
    }

    /// <summary>Mensaje del rechazo para <c>SapMargenSyncLog</c> (<c>null</c> si las filas alcanzan).</summary>
    public static string? Rechazo(int recibidas, int minimoFilas, int porcentajeActivas, int filasActivas)
    {
        var minimo = MinimoExigido(minimoFilas, porcentajeActivas, filasActivas);
        return recibidas >= minimo
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"SAP TM devolvió {recibidas} filas y el mínimo exigido es {minimo} (máximo entre SapTm:MinimoFilas = {minimoFilas} y el {porcentajeActivas} % de las {filasActivas} filas activas): no se tocó la tabla.");
    }
}
