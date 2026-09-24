using System.Collections.Frozen;
using System.Text;

namespace Cotifly.Dominio.Comun;

/// <summary>
/// Traduce los enums del dominio a su código textual estable y de vuelta.
/// El código es el nombre del miembro en snake_case minúsculas (<c>SinEnviar</c> → <c>sin_enviar</c>,
/// <c>SapTm</c> → <c>sap_tm</c>, <c>GerenteComercial</c> → <c>gerente_comercial</c>): es el valor que
/// guardan las columnas de <c>dbo_cotifly</c> y el que expone el contrato #17.
/// </summary>
public static class clsCodigoEnum
{
    /// <summary>Código textual del valor (ej. <c>sin_enviar</c>).</summary>
    public static string ACodigo<TEnum>(TEnum valor) where TEnum : struct, Enum =>
        clsCache<TEnum>.Codigos.TryGetValue(valor, out var codigo)
            ? codigo
            : throw new ArgumentOutOfRangeException(nameof(valor), valor, $"Valor no definido en {typeof(TEnum).Name}.");

    /// <summary>Valor del enum a partir de su código. Lanza <see cref="ArgumentException"/> si no existe.</summary>
    public static TEnum DesdeCodigo<TEnum>(string codigo) where TEnum : struct, Enum =>
        TryDesdeCodigo<TEnum>(codigo, out var valor)
            ? valor
            : throw new ArgumentException($"'{codigo}' no es un código válido de {typeof(TEnum).Name}.", nameof(codigo));

    /// <summary>Intenta obtener el valor del enum a partir de su código (sin distinguir mayúsculas).</summary>
    public static bool TryDesdeCodigo<TEnum>(string? codigo, out TEnum valor) where TEnum : struct, Enum
    {
        if (codigo is not null && clsCache<TEnum>.Valores.TryGetValue(codigo.Trim(), out valor))
        {
            return true;
        }

        valor = default;
        return false;
    }

    /// <summary>Códigos válidos de un enum, en el orden de declaración.</summary>
    public static IReadOnlyList<string> Codigos<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(ACodigo).ToArray();

    internal static string ASnakeCase(string nombre)
    {
        var sb = new StringBuilder(nombre.Length + 4);
        for (var i = 0; i < nombre.Length; i++)
        {
            var c = nombre[i];
            if (char.IsUpper(c) && i > 0)
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    private static class clsCache<TEnum> where TEnum : struct, Enum
    {
        public static readonly FrozenDictionary<TEnum, string> Codigos =
            Enum.GetValues<TEnum>().ToFrozenDictionary(v => v, v => ASnakeCase(v.ToString()));

        public static readonly FrozenDictionary<string, TEnum> Valores =
            Codigos.ToFrozenDictionary(par => par.Value, par => par.Key, StringComparer.OrdinalIgnoreCase);
    }
}
