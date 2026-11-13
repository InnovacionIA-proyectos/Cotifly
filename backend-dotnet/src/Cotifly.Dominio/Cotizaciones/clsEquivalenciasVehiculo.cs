namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Cotifly guarda el código de vehículo ORIGINAL (el del cubicador, o el que digitó el comercial) y a Nexo viaja el código
/// oficial que resolvió el diccionario de alias de Nexo (<c>C2</c> → <c>2</c>). El envío registra esas traducciones (código de Cotifly →
/// código enviado) y el polling compara contra el código ENVIADO, no contra el guardado:
/// <list type="bullet">
/// <item>el emparejamiento por contenido ve el camión con el código que viajó (<see cref="Enviado"/>), así que <c>C2</c> empareja con la línea
/// de Nexo que dice <c>2</c>;</item>
/// <item>la regla "la Mesa manda" recibe el código guardado cuando Nexo devuelve el mismo código que se le envió (<see cref="DeNexo"/>):
/// no es un cambio de la Mesa, el camión conserva <c>C2</c> (y sus porcentajes de llenado) y la lectura siguiente tampoco cambia nada. Si la
/// Mesa sí cambia el vehículo (<c>2</c> → <c>3S3</c>), el cambio pasa como siempre.</item>
/// </list>
/// Por qué guardar el original y no el traducido: el nombre del vehículo en el PDF y en el Excel sale del catálogo del cubicador (por su
/// código), el original es lo que recomendó el cubicador, y una traducción que dependa del diccionario del momento haría que una lectura
/// sin diccionario viera un "cambio". La traducción queda fija en el registro del envío.
/// </summary>
public static class clsEquivalenciasVehiculo
{
    /// <summary>
    /// El código que viajó a Nexo por <paramref name="codigo"/>: su traducción si el envío registró una (exacta o la misma sigla escrita de
    /// otra forma, <see cref="clsVehiculoLineaSolicitud.MismoCodigo"/>), o el mismo código.
    /// </summary>
    public static string? Enviado(string? codigo, IReadOnlyDictionary<string, string>? equivalencias)
    {
        if (string.IsNullOrWhiteSpace(codigo) || equivalencias is null || equivalencias.Count == 0)
        {
            return codigo;
        }

        var buscado = codigo.Trim();
        if (equivalencias.TryGetValue(buscado, out var exacto))
        {
            return exacto;
        }

        foreach (var (original, enviado) in equivalencias)
        {
            if (clsVehiculoLineaSolicitud.MismoCodigo(original, buscado))
            {
                return enviado;
            }
        }

        return codigo;
    }

    /// <summary>
    /// El vehículo de la línea de Nexo visto desde Cotifly: si es el mismo código que viajó por <paramref name="actual"/> (su traducción
    /// por alias), devuelve <paramref name="actual"/> (no hay cambio de la Mesa); si no, <paramref name="deNexo"/> tal cual.
    /// </summary>
    public static string? DeNexo(string? actual, string? deNexo, IReadOnlyDictionary<string, string>? equivalencias)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(deNexo))
        {
            return deNexo;
        }

        var enviado = Enviado(actual, equivalencias);
        return enviado is not null && !clsVehiculoLineaSolicitud.MismoCodigo(enviado, actual) && clsVehiculoLineaSolicitud.MismoCodigo(enviado, deNexo)
            ? actual
            : deNexo;
    }
}
