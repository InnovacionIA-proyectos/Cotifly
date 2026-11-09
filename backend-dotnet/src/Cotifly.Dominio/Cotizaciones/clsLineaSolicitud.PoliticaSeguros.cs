using Cotifly.Dominio.Seguros;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>HU-046.3 (#269): lo que exige la política de seguros a la ruta.</summary>
public sealed partial class clsLineaSolicitud
{
    /// <summary>
    /// La evaluación de la política para esta ruta: tramo con el valor TOTAL de la mercancía de la ruta (decisión 2: no se divide por
    /// camiones) y las obligatorias cruzadas con los <c>codigoServicio</c> de sus conexos. Decisión del usuario (2026-11-09):
    /// <paramref name="cuenta"/> dice qué conexos cumplen (el caso de uso excluye los <c>fallido_definitivo</c> e <c>incierto</c> en Nexo);
    /// sin él cuentan todos.
    /// </summary>
    public clsEvaluacionPoliticaSeguro EvaluarPoliticaSeguros(clsPoliticaSeguros politica, Func<clsServicioConexoRuta, bool>? cuenta = null)
    {
        ArgumentNullException.ThrowIfNull(politica);
        return politica.Evaluar(ValorMercancia, _conexos.Where(c => cuenta?.Invoke(c) ?? true).Select(c => c.CodigoServicio));
    }
}
