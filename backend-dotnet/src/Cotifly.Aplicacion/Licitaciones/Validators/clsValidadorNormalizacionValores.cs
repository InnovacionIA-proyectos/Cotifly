using Cotifly.Aplicacion.Licitaciones.DTOs;
using Cotifly.Dominio.Comun;

namespace Cotifly.Aplicacion.Licitaciones.Validators;

/// <summary>
/// HU-044.4 (#255): validación local de <c>POST /web/licitaciones/normalizar-valores</c> ANTES de llamar a NEXO, con los mismos límites
/// y códigos que <c>normalizar-valores</c> de NEXO (máx. 1000 valores de 150 caracteres, campos cerrados). Así una petición mal armada no
/// gasta la llamada (ni el cupo de IA del comercial). La clase de contenedor no se normaliza aquí: sigue por <c>GET /web/alias/resolver</c>
/// (decisión de Sara del 2026-10-29, ADR-028 §8).
/// </summary>
public static class clsValidadorNormalizacionValores
{
    /// <summary>Máximo de valores por petición (el de NEXO).</summary>
    public const int MaximoValores = 1000;

    /// <summary>Largo máximo de un valor (el de NEXO).</summary>
    public const int LargoMaximoValor = 150;

    /// <summary>Nombres con los que el front podría pedir la clase de contenedor: reciben un mensaje que dice adónde ir.</summary>
    private static readonly HashSet<string> _contenedor = new(StringComparer.OrdinalIgnoreCase) { "clase_contenedor", "claseContenedor", "contenedor" };

    /// <summary>
    /// Los valores distintos de la entrada (mismo campo y mismo texto, sin distinguir los espacios de los extremos), en el orden en que
    /// llegaron, o el primer error: <c>VALORES_REQUERIDOS</c> (sin valores o uno vacío), <c>DEMASIADOS_VALORES</c>,
    /// <c>CAMPO_INVALIDO</c> o <c>VALOR_DEMASIADO_LARGO</c>, con la posición (base 1) en el mensaje.
    /// </summary>
    public static clsResultado<IReadOnlyList<clsValorEntradaNormalizacion>> Validar(clsEntradaNormalizacion entrada)
    {
        ArgumentNullException.ThrowIfNull(entrada);
        var valores = entrada.Valores;
        if (valores is null || valores.Count == 0)
        {
            return new clsErrorDominio(clsCodigosError.ValoresRequeridos, "Envía al menos un valor a normalizar.");
        }

        if (valores.Count > MaximoValores)
        {
            return new clsErrorDominio(clsCodigosError.DemasiadosValores,
                $"Se pueden normalizar hasta {MaximoValores} valores por petición; llegaron {valores.Count}. Envía solo los distintos.");
        }

        var distintos = new List<clsValorEntradaNormalizacion>(valores.Count);
        var vistos = new HashSet<(string, string)>();
        for (var i = 0; i < valores.Count; i++)
        {
            var posicion = i + 1;
            var valor = valores[i];
            if (valor is null || string.IsNullOrWhiteSpace(valor.Campo) || clsCamposNormalizacion.ANexo(valor.Campo.Trim()) is null)
            {
                return new clsErrorDominio(clsCodigosError.CampoInvalido, MensajeCampoInvalido(posicion, valor?.Campo));
            }

            if (string.IsNullOrWhiteSpace(valor.Valor))
            {
                return new clsErrorDominio(clsCodigosError.ValoresRequeridos, $"El valor {posicion} está vacío: envía solo celdas con texto.");
            }

            var texto = valor.Valor.Trim();
            if (texto.Length > LargoMaximoValor)
            {
                return new clsErrorDominio(clsCodigosError.ValorDemasiadoLargo,
                    $"El valor {posicion} supera {LargoMaximoValor} caracteres.");
            }

            var campo = valor.Campo.Trim();
            if (vistos.Add((campo, texto)))
            {
                distintos.Add(new clsValorEntradaNormalizacion(campo, texto));
            }
        }

        return distintos;
    }

    private static string MensajeCampoInvalido(int posicion, string? campo)
    {
        var permitidos = string.Join(", ", clsCamposNormalizacion.Todos);
        if (campo is not null && _contenedor.Contains(campo.Trim()))
        {
            return $"El valor {posicion}: la clase de contenedor no se normaliza aquí; usa GET /web/alias/resolver?tipo=contenedor. Campos: {permitidos}.";
        }

        return string.IsNullOrWhiteSpace(campo)
            ? $"El valor {posicion} no trae el campo. Campos: {permitidos}."
            : $"El valor {posicion} tiene un campo que no se normaliza ('{campo.Trim()}'). Campos: {permitidos}.";
    }
}
